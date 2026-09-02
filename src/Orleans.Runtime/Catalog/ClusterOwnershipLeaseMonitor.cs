using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainReferences;
using Orleans.Internal;
using Orleans.Runtime.Placement;

namespace Orleans.Runtime;

internal sealed class ClusterOwnershipLeaseMonitor : ILifecycleParticipant<ISiloLifecycle>
{
    private readonly ConcurrentDictionary<ActivationId, IGrainContext> _activations = new();
    private readonly ConcurrentDictionary<ActivationId, TaskCompletionSource> _renewingActivations = new();
    private readonly ClusterLocatorResolver _locatorResolver;
    private readonly UniversalReferenceBindingResolver _bindingResolver;
    private readonly IAsyncTimer _timer;
    private readonly ILogger<ClusterOwnershipLeaseMonitor> _logger;
    private readonly TimeSpan _period;
    private readonly bool _enabled;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _runTask;

    public ClusterOwnershipLeaseMonitor(
        ClusterLocatorResolver locatorResolver,
        UniversalReferenceBindingResolver bindingResolver,
        IAsyncTimerFactory timerFactory,
        IOptions<MetaclusterOptions> options,
        ILogger<ClusterOwnershipLeaseMonitor> logger,
        [FromKeyedServices(TimeProviderNames.SystemTimers)] TimeProvider timeProvider)
    {
        _locatorResolver = locatorResolver;
        _bindingResolver = bindingResolver;
        _logger = logger;
        _enabled = options.Value.Enabled;
        var renewalWindow = options.Value.ClusterOwnershipLeaseRenewalWindow;
        _period = renewalWindow > TimeSpan.Zero
            ? TimeSpan.FromTicks(Math.Max(1, renewalWindow.Ticks / 2))
            : TimeSpan.FromSeconds(1);
        _timer = timerFactory.Create(_period, nameof(ClusterOwnershipLeaseMonitor), timeProvider);
    }

    public void Track(IGrainContext context)
    {
        if (!_enabled)
        {
            return;
        }

        context.ObservableLifecycle.Subscribe<ClusterOwnershipLeaseMonitor>(
            GrainLifecycleStage.First,
            cancellationToken => ValidateAndTrack(context, cancellationToken));
    }

    public void Untrack(IGrainContext context) => _activations.TryRemove(context.ActivationId, out _);

    private async Task ValidateAndTrack(IGrainContext context, CancellationToken cancellationToken)
    {
        if (_locatorResolver.Resolve(context.GrainId.Type) is not IClusterOwnershipValidator validator)
        {
            return;
        }

        var ownership = await validator.ValidateLocalOwnership(
            context.GrainId,
            _bindingResolver.ClusterId,
            cancellationToken);
        context.SetComponent(ownership);
        _activations[context.ActivationId] = context;
    }

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(
            nameof(ClusterOwnershipLeaseMonitor),
            ServiceLifecycleStage.BecomeActive,
            Start,
            Stop);
    }

    private Task Start(CancellationToken cancellationToken)
    {
        if (_enabled)
        {
            _runTask = Task.Run(Run);
        }

        return Task.CompletedTask;
    }

    private async Task Stop(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        _timer.Dispose();
        if (_runTask is not null)
        {
            await _runTask.WaitAsync(cancellationToken).SuppressThrowing();
        }

        var renewals = new List<Task>(_renewingActivations.Count);
        foreach (var renewal in _renewingActivations.Values)
        {
            renewals.Add(renewal.Task);
        }

        await Task.WhenAll(renewals).WaitAsync(cancellationToken).SuppressThrowing();
    }

    private async Task Run()
    {
        while (await _timer.NextTick(_period))
        {
            foreach (var context in _activations.Values)
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_renewingActivations.TryAdd(context.ActivationId, completion))
                {
                    Renew(context, completion).Ignore();
                }
            }
        }
    }

    private async Task Renew(IGrainContext context, TaskCompletionSource completion)
    {
        try
        {
            if (_locatorResolver.Resolve(context.GrainId.Type) is not IClusterOwnershipValidator validator)
            {
                Untrack(context);
                return;
            }

            var ownership = await validator.ValidateLocalOwnership(
                context.GrainId,
                _bindingResolver.ClusterId,
                _stopping.Token);
            context.SetComponent(ownership);
        }
        catch (Exception exception)
        {
            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            Untrack(context);
            _logger.LogWarning(
                exception,
                "Deactivating grain {GrainId} because cluster ownership could not be renewed.",
                context.GrainId);
            context.Deactivate(
                new DeactivationReason(
                    DeactivationReasonCode.DirectoryFailure,
                    exception,
                    "The cluster ownership lease expired or could not be renewed."));
        }
        finally
        {
            _renewingActivations.TryRemove(context.ActivationId, out _);
            completion.TrySetResult();
        }
    }
}
