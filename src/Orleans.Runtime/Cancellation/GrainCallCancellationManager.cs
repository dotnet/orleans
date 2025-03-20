#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime;

/// <summary>
/// Remote interface for cancelling grain calls.
/// </summary>
internal interface IGrainCallCancellationManagerSystemTarget : ISystemTarget
{
    /// <summary>
    /// Cancels a collection of grain calls.
    /// </summary>
    ValueTask CancelCallsAsync([Immutable] List<GrainCallCancellationRequest> cancellationRequests);
}

[GenerateSerializer, Immutable]
internal struct GrainCallCancellationRequest(GrainId targetGrainId, GrainId sourceGrainId, CorrelationId messageId)
{
    [Id(0)]
    public GrainId TargetGrainId { get; set; } = targetGrainId;

    [Id(1)]
    public GrainId SourceGrainId { get; set; } = sourceGrainId;

    [Id(2)]
    public CorrelationId MessageId { get; set; } = messageId;
}

/// <summary>
/// Cancels grain calls issued to remote hosts and handles cancellation requests from other hosts.
/// </summary>
internal class GrainCallCancellationManager : SystemTarget, IGrainCallCancellationManagerSystemTarget, IGrainCallCancellationManager, ILifecycleParticipant<ISiloLifecycle>
{
    private const int MaxBatchSize = 1_000;
    private readonly ConcurrentDictionary<SiloAddress, (Task PumpTask, Channel<GrainCallCancellationRequest> WorkItemChannel, CancellationTokenSource Cts)> _workers = new();
    private readonly CancellationTokenSource _shuttingDownCts = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<GrainCallCancellationManager> _logger;
    private readonly Catalog _catalog;
    private readonly ActivationDirectory _activationDirectory;
    private readonly IClusterMembershipService _clusterMembershipService;
    private readonly object _lock = new();
    private readonly Task? _membershipUpdatesTask;
    private IInternalGrainFactory? _grainFactory;

    public GrainCallCancellationManager(
        ILocalSiloDetails localSiloDetails,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        Catalog catalog,
        ActivationDirectory activationDirectory,
        IClusterMembershipService clusterMembershipService) : base(Constants.CancellationManagerType, localSiloDetails.SiloAddress, loggerFactory)
    {
        _serviceProvider = serviceProvider;
        _logger = loggerFactory.CreateLogger<GrainCallCancellationManager>();
        _catalog = catalog;
        _activationDirectory = activationDirectory;
        _clusterMembershipService = clusterMembershipService;
        _catalog.RegisterSystemTarget(this);

        {
            using var _ = new ExecutionContextSuppressor();
            _membershipUpdatesTask = Task.Factory.StartNew(
                state => ((GrainCallCancellationManager)state!).ProcessMembershipUpdates(),
                this,
                CancellationToken.None,
                TaskCreationOptions.None,
                WorkItemGroup.TaskScheduler).Unwrap();
            _membershipUpdatesTask.Ignore();
        }
    }

    private IInternalGrainFactory GrainFactory => _grainFactory ??= _serviceProvider.GetRequiredService<IInternalGrainFactory>();

    public ValueTask CancelCallsAsync(List<GrainCallCancellationRequest> cancellationRequests)
    {
        foreach (var request in cancellationRequests)
        {
            // Try to directly call the cancellation method locally
            ValueTask cancellationTask;
            if (_activationDirectory.FindTarget(request.TargetGrainId) is IGrainCallCancellationExtension extension)
            {
                cancellationTask = extension.CancelRequestAsync(request.SourceGrainId, request.MessageId);
            }
            else
            {
                // Fall back to a regular grain call.
                cancellationTask = GrainFactory.GetGrain<IGrainCallCancellationExtension>(request.TargetGrainId).CancelRequestAsync(request.SourceGrainId, request.MessageId);
            }

            if (!cancellationTask.IsCompletedSuccessfully)
            {
                cancellationTask.AsTask().Ignore();
            }
            else
            {
                // Observe the completed task.
                cancellationTask.GetAwaiter().GetResult();
            }
        }

        return ValueTask.CompletedTask;
    }

    public void SignalCancellation(SiloAddress? targetSilo, GrainId targetGrainId, GrainId sourceGrainId, CorrelationId messageId)
    {
        if (targetSilo is not null
            && GetOrCreateWorker(targetSilo).Writer.TryWrite(new GrainCallCancellationRequest(targetGrainId, sourceGrainId, messageId)))
        {
            return;
        }

        var request = GrainFactory.GetGrain<IGrainCallCancellationExtension>(targetGrainId).CancelRequestAsync(sourceGrainId, messageId);
        if (!request.IsCompleted)
        {
            request.AsTask().Ignore();
        }
    }

    private async Task ProcessMembershipUpdates()
    {
        await Task.Yield();

        try
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Monitoring cluster membership updates");
            }

            var previousSnapshot = _clusterMembershipService.CurrentSnapshot;
            await foreach (var snapshot in _clusterMembershipService.MembershipUpdates.WithCancellation(_shuttingDownCts.Token))
            {
                try
                {
                    var diff = snapshot.CreateUpdate(previousSnapshot);
                    previousSnapshot = snapshot;
                    foreach (var change in diff.Changes)
                    {
                        if (change.Status is SiloStatus.Dead or SiloStatus.None
                            && _workers.TryGetValue(change.SiloAddress, out var worker))
                        {
                            worker.WorkItemChannel.Writer.TryComplete();
                            try
                            {
                                worker.Cts.Cancel(throwOnFirstException: false);
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Error processing cluster membership updates");
                }
            }
        }
        finally
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("No longer monitoring cluster membership updates");
            }
        }
    }

    private async Task PumpCancellationQueue(SiloAddress targetSilo, Channel<GrainCallCancellationRequest> workItems, CancellationToken cancellationToken)
    {
        try
        {
            var remote = GrainFactory.GetSystemTarget<IGrainCallCancellationManagerSystemTarget>(Constants.CancellationManagerType, targetSilo);
            await Task.Yield();

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Starting cancellation worker for target silo {SiloAddress}", targetSilo);
            }

            var batch = new List<GrainCallCancellationRequest>();
            var reader = workItems.Reader;
            while (await reader.WaitToReadAsync(cancellationToken))
            {
                try
                {
                    // Collect a batch of work items.
                    while (batch.Count < MaxBatchSize && reader.TryRead(out var workItem))
                    {
                        batch.Add(workItem);
                    }

                    // Attempt to cancel the batch.
                    await remote.CancelCallsAsync(batch).AsTask().WaitAsync(cancellationToken);

                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("Cancelled {Count} requests to target silo {SiloAddress}", batch.Count, targetSilo);
                    }

                    batch.Clear();
                }
                catch (Exception exception)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    _logger.LogError(exception, "Error while cancelling {Count} requests to {SiloAddress}", batch.Count, targetSilo);
                    await Task.Delay(5_000, cancellationToken);
                }
            }
        }
        catch
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
        finally
        {
            // Remove ourselves and clean up.
            RemoveWorker(targetSilo);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Exiting cancellation worker for target silo {SiloAddress}", targetSilo);
            }
        }
    }

    private (ChannelWriter<GrainCallCancellationRequest> Writer, CancellationTokenSource Cts) GetOrCreateWorker(SiloAddress targetSilo)
    {
        if (!_workers.TryGetValue(targetSilo, out var worker))
        {
            lock (_lock)
            {
                if (!_workers.TryGetValue(targetSilo, out worker))
                {
                    using var _ = new ExecutionContextSuppressor();
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(_shuttingDownCts.Token);
                    var channel = Channel.CreateUnbounded<GrainCallCancellationRequest>();
                    var pumpTask = Task.Factory.StartNew(
                        () => PumpCancellationQueue(targetSilo, channel, cts.Token),
                        CancellationToken.None,
                        TaskCreationOptions.None,
                        WorkItemGroup.TaskScheduler).Unwrap();
                    pumpTask.Ignore();

                    worker = (pumpTask, channel, cts);
                    var didAdd = _workers.TryAdd(targetSilo, worker);
                    Debug.Assert(didAdd);
                }
            }
        }

        return (worker.WorkItemChannel.Writer, worker.Cts);
    }

    private void RemoveWorker(SiloAddress targetSilo)
    {
        if (_workers.TryRemove(targetSilo, out var entry))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Target silo '{SiloAddress}' is no longer active, so this cancellation activation worker is terminating", targetSilo);
            }

            entry.Cts.Dispose();
        }
    }

    private Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    private async Task StopAsync(CancellationToken cancellationToken)
    {
        var tasks = new List<Task>();
        if (_membershipUpdatesTask is { } task)
        {
            tasks.Add(task);
        }

        foreach (var (_, worker) in _workers)
        {
            worker.WorkItemChannel.Writer.TryComplete();
            try
            {
                worker.Cts.Cancel(throwOnFirstException: false);
            }
            catch
            {
            }
            tasks.Add(worker.PumpTask);
        }

        try
        {
            _shuttingDownCts.Cancel(throwOnFirstException: false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Error signaling shutdown.");
        }

        await Task.WhenAll(tasks).WaitAsync(cancellationToken).SuppressThrowing();
    }

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
    {
        _ = GrainFactory;
        lifecycle.Subscribe(
            nameof(GrainCallCancellationManager),
            ServiceLifecycleStage.RuntimeGrainServices,
                ct => this.RunOrQueueTask(() => StartAsync(ct)),
                ct => this.RunOrQueueTask(() => StopAsync(ct)));
    }
}
