#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Orleans.Internal;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime;

/// <summary>
/// Remote interface for migrating grain activations to a silo.
/// </summary>
internal interface IActivationMigrationManagerSystemTarget : ISystemTarget
{
    /// <summary>
    /// Accepts migrating grains on a best-effort basis.
    /// </summary>
    ValueTask AcceptMigratingGrains([Immutable] List<GrainMigrationPackage> migratingGrains);
}

[GenerateSerializer, Immutable]
internal struct GrainMigrationPackage
{
    [Id(0)]
    public GrainId GrainId { get; set; }

    [Id(1)]
    public MigrationContext MigrationContext { get; set; }
}

/// <summary>
/// Functionality for migrating an activation to a new location.
/// </summary>
internal interface IActivationMigrationManager
{
    /// <summary>
    /// Attempts to migrate a grain to the specified target.
    /// </summary>
    /// <param name="target">The migration target.</param>
    /// <param name="grainId">The grain being migrated.</param>
    /// <param name="migrationContext">Information about the grain being migrated, which will be consumed by the new activation.</param>
    ValueTask MigrateAsync(SiloAddress target, GrainId grainId, MigrationContext migrationContext);
}

/// <summary>
/// Migrates grain activations to target hosts and handles migration requests from other hosts.
/// </summary>
internal sealed partial class ActivationMigrationManager : SystemTarget, IActivationMigrationManagerSystemTarget, IActivationMigrationManager, ILifecycleParticipant<ISiloLifecycle>
{
    private const int MaxBatchSize = 1_000;
    private readonly ConcurrentDictionary<SiloAddress, (Task PumpTask, Channel<MigrationWorkItem> WorkItemChannel)> _workers = new();
    private readonly ObjectPool<MigrationWorkItem> _workItemPool = ObjectPool.Create(new MigrationWorkItem.ObjectPoolPolicy());
    private readonly CancellationTokenSource _shuttingDownCts = new();
    private readonly ILogger<ActivationMigrationManager> _logger;
    private readonly IInternalGrainFactory _grainFactory;
    private readonly Catalog _catalog;
    private readonly IClusterMembershipService _clusterMembershipService;
    private readonly object _lock = new();

#pragma warning disable IDE0052 // Remove unread private members. Justification: this field is only for diagnostic purposes.
    private readonly Task? _membershipUpdatesTask;
#pragma warning restore IDE0052 // Remove unread private members

    public ActivationMigrationManager(
        ILocalSiloDetails localSiloDetails,
        ILoggerFactory loggerFactory,
        IInternalGrainFactory grainFactory,
        Catalog catalog,
        SystemTargetShared shared,
        IClusterMembershipService clusterMembershipService) : base(Constants.ActivationMigratorType, shared)
    {
        _grainFactory = grainFactory;
        _logger = loggerFactory.CreateLogger<ActivationMigrationManager>();
        _catalog = catalog;
        _clusterMembershipService = clusterMembershipService;
        shared.ActivationDirectory.RecordNewTarget(this);

        {
            using var _ = new ExecutionContextSuppressor();
            _membershipUpdatesTask = Task.Factory.StartNew(
                state => ((ActivationMigrationManager)state!).ProcessMembershipUpdates(),
                this,
                CancellationToken.None,
                TaskCreationOptions.None,
                WorkItemGroup.TaskScheduler).Unwrap();
            _membershipUpdatesTask.Ignore();
        }
    }

    public async ValueTask AcceptMigratingGrains(List<GrainMigrationPackage> migratingGrains)
    {
        var activations = new List<ActivationData>();
        foreach (var package in migratingGrains)
        {
            // If the activation does not exist, create it and provide it with the migration context while doing so.
            // If the activation already exists or cannot be created, it is too late to perform migration, so ignore the request.
            var context = _catalog.GetOrCreateActivation(package.GrainId, requestContextData: null, package.MigrationContext);
            if (context is ActivationData activation)
            {
                activations.Add(activation);
            }
        }

        while (true)
        {
            var allActiveOrTerminal = true;
            foreach (var activation in activations)
            {
                lock (activation)
                {
                    if (activation.State is not (ActivationState.Valid or ActivationState.Invalid))
                    {
                        allActiveOrTerminal = false;
                        break;
                    }
                }
            }

            if (allActiveOrTerminal)
            {
                break;
            }

            // Wait a short amount of time and poll the activations again.
            await Task.Delay(5);
        }
    }

    public ValueTask MigrateAsync(SiloAddress targetSilo, GrainId grainId, MigrationContext migrationContext)
    {
        var workItem = _workItemPool.Get();
        var migrationPackage = new GrainMigrationPackage { GrainId = grainId, MigrationContext = migrationContext };
        workItem.Initialize(migrationPackage);
        var workItemWriter = GetOrCreateWorker(targetSilo);
        if (!workItemWriter.TryWrite(workItem))
        {
            workItem.SetException(new SiloUnavailableException($"Silo {targetSilo} is no longer active"));
        }

        return workItem.AsValueTask();
    }

    private async Task ProcessMembershipUpdates()
    {
        await Task.Yield();

        try
        {
            LogDebugMonitoringUpdates();

            var previousSnapshot = _clusterMembershipService.CurrentSnapshot;
            await foreach (var snapshot in _clusterMembershipService.MembershipUpdates)
            {
                try
                {
                    var diff = snapshot.CreateUpdate(previousSnapshot);
                    previousSnapshot = snapshot;
                    foreach (var change in diff.Changes)
                    {
                        if (change.Status.IsTerminating())
                        {
                            RemoveWorker(change.SiloAddress);
                        }
                    }
                }
                catch (Exception exception)
                {
                    LogErrorProcessingMembershipUpdates(exception);
                }
            }
        }
        finally
        {
            LogDebugNoLongerMonitoring();
        }
    }

    private async Task PumpMigrationQueue(SiloAddress targetSilo, Channel<MigrationWorkItem> workItems)
    {
        try
        {
            var remote = _grainFactory.GetSystemTarget<IActivationMigrationManagerSystemTarget>(Constants.ActivationMigratorType, targetSilo);
            await Task.Yield();

            LogDebugStartingWorker(targetSilo);

            var items = new List<MigrationWorkItem>();
            var batch = new List<GrainMigrationPackage>();
            var reader = workItems.Reader;
            while (await reader.WaitToReadAsync())
            {
                try
                {
                    // Collect a batch of work items.
                    while (batch.Count < MaxBatchSize && reader.TryRead(out var workItem))
                    {
                        items.Add(workItem);
                        batch.Add(workItem.Value);
                    }

                    // Attempt to migrate the batch.
                    await remote.AcceptMigratingGrains(batch).AsTask().WaitAsync(_shuttingDownCts.Token);

                    foreach (var item in items)
                    {
                        item.SetCompleted();
                    }

                    LogDebugMigratedActivations(items.Count, targetSilo);
                }
                catch (Exception exception)
                {
                    if (!_shuttingDownCts.IsCancellationRequested)
                    {
                        LogErrorMigratingActivations(exception, items.Count, targetSilo);
                    }

                    foreach (var item in items)
                    {
                        item.SetException(exception);
                    }

                    // If the silo is terminating, we should stop trying to migrate activations to it.
                    if (_clusterMembershipService.CurrentSnapshot.GetSiloStatus(targetSilo).IsTerminating())
                    {
                        break;
                    }
                }
                finally
                {
                    items.Clear();
                    batch.Clear();
                }
            }

            // Remove ourselves and clean up.
            RemoveWorker(targetSilo);
        }
        finally
        {
            LogDebugExitingWorker(targetSilo);
        }
    }

    private ChannelWriter<MigrationWorkItem> GetOrCreateWorker(SiloAddress targetSilo)
    {
        if (!_workers.TryGetValue(targetSilo, out var entry))
        {
            lock (_lock)
            {
                if (!_workers.TryGetValue(targetSilo, out entry))
                {
                    using var _ = new ExecutionContextSuppressor();
                    var channel = Channel.CreateUnbounded<MigrationWorkItem>();
                    var pumpTask = Task.Factory.StartNew(
                        () => PumpMigrationQueue(targetSilo, channel),
                        CancellationToken.None,
                        TaskCreationOptions.None,
                        WorkItemGroup.TaskScheduler).Unwrap();
                    pumpTask.Ignore();

                    entry = (pumpTask, channel);
                    var didAdd = _workers.TryAdd(targetSilo, entry);
                    Debug.Assert(didAdd);
                }
            }
        }

        return entry.WorkItemChannel.Writer;
    }

    private void RemoveWorker(SiloAddress targetSilo)
    {
        if (_workers.TryRemove(targetSilo, out var entry))
        {
            LogDebugTargetSilo(targetSilo);

            entry.WorkItemChannel.Writer.TryComplete();

            var exception = new SiloUnavailableException($"Silo {targetSilo} is no longer active");
            while (entry.WorkItemChannel.Reader.TryRead(out var item))
            {
                item.SetException(exception);
            }
        }
    }

    private Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    private async Task StopAsync(CancellationToken cancellationToken)
    {
        var workerTasks = new List<Task>();
        foreach (var (_, value) in _workers)
        {
            value.WorkItemChannel.Writer.TryComplete();
            workerTasks.Add(value.PumpTask);
        }

        try
        {
            _shuttingDownCts.Cancel();
        }
        catch (Exception exception)
        {
            LogWarningSignalShutdownError(exception);
        }

        await Task.WhenAll(workerTasks).WaitAsync(cancellationToken).SuppressThrowing();
    }

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(
            nameof(ActivationMigrationManager),
            ServiceLifecycleStage.RuntimeGrainServices,
                ct => this.RunOrQueueTask(() => StartAsync(ct)),
                ct => this.RunOrQueueTask(() => StopAsync(ct)));
    }

    private class MigrationWorkItem : IValueTaskSource
    {
        private ManualResetValueTaskSourceCore<int> _core = new() { RunContinuationsAsynchronously = true };
        private GrainMigrationPackage _migrationPackage;

        public void Initialize(GrainMigrationPackage package) => _migrationPackage = package;
        public void Reset() => _core.Reset();

        public GrainMigrationPackage Value => _migrationPackage;

        public void SetCompleted() => _core.SetResult(0);
        public void SetException(Exception exception) => _core.SetException(exception);
        public ValueTask AsValueTask() => new (this, _core.Version);

        public void GetResult(short token)
        {
            try
            {
                _core.GetResult(token);
            }
            finally
            {
                Reset();
            }
        }

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _core.OnCompleted(continuation, state, token, flags);

        public sealed class ObjectPoolPolicy : IPooledObjectPolicy<MigrationWorkItem>
        {
            public MigrationWorkItem Create() => new();
            public bool Return(MigrationWorkItem obj)
            {
                obj.Reset();
                return true;
            }
        }
    }

    // Log value types
    private readonly struct SiloAddressLogValue
    {
        private readonly SiloAddress _silo;
        public SiloAddressLogValue(SiloAddress silo) => _silo = silo;
        public override string ToString() => _silo.ToStringWithHashCode();
    }

    // Logger methods at end of class
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Monitoring cluster membership updates")]
    private partial void LogDebugMonitoringUpdates();

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error processing cluster membership updates"
    )]
    private partial void LogErrorProcessingMembershipUpdates(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "No longer monitoring cluster membership updates")]
    private partial void LogDebugNoLongerMonitoring();

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Starting migration worker for target silo {SiloAddress}")]
    private partial void LogDebugStartingWorker(SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Migrated {Count} activations to target silo {SiloAddress}")]
    private partial void LogDebugMigratedActivations(int count, SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error while migrating {Count} grain activations to {SiloAddress}")]
    private partial void LogErrorMigratingActivations(Exception exception, int count, SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Exiting migration worker for target silo {SiloAddress}")]
    private partial void LogDebugExitingWorker(SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Target silo {SiloAddress} is no longer active, so this migration activation worker is terminating"
    )]
    private partial void LogDebugTargetSilo(SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Error signaling shutdown"
    )]
    private partial void LogWarningSignalShutdownError(Exception exception);
}
