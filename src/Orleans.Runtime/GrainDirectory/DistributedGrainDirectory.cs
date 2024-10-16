using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.GrainDirectory;
using Orleans.Internal;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Scheduler;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

internal sealed partial class DistributedGrainDirectory : SystemTarget, IGrainDirectory, IGrainDirectoryClient, ILifecycleParticipant<ISiloLifecycle>, DistributedGrainDirectory.ITestHooks
{
    private readonly DirectoryMembershipService _membershipService;
    private readonly ILogger<DistributedGrainDirectory> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ImmutableArray<GrainDirectoryReplica> _partitions;
    private readonly CancellationTokenSource _stoppedCts = new();

    internal CancellationToken OnStoppedToken => _stoppedCts.Token;
    internal ClusterMembershipSnapshot ClusterMembershipSnapshot => _membershipService.CurrentView.ClusterMembershipSnapshot;

    // The recovery membership value is used to avoid a race between concurrent registration & recovery operations which could lead to lost registrations.
    // This could occur when a new activation is created and begins registering itself with a host which crashes. Concurrently, the new owner initiates
    // recovery and asks all silos for their activations. When this silo processes this request, it will have the activation in its internal
    // 'ActivationDirectory' even though these activations may not yet have completed registration. Therefore, multiple silos may return an entry for the same
    // grain. By ensuring that any registration occurred at a version at least as high as the recovery version, we avoid this issue. This could be made more
    // precise by also tracking the sets of ranges which need to be recovered, but that complicates things somewhat since it would require tracking the ranges
    // for each recovery version.
    private long _recoveryMembershipVersion;
    private Task _runTask = Task.CompletedTask;

    public DistributedGrainDirectory(
        DirectoryMembershipService membershipService,
        ILogger<DistributedGrainDirectory> logger,
        ILocalSiloDetails localSiloDetails,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        IInternalGrainFactory grainFactory) : base(Constants.GrainDirectory, localSiloDetails.SiloAddress, loggerFactory)
    {
        _serviceProvider = serviceProvider;
        _membershipService = membershipService;
        _logger = logger;
        var partitions = ImmutableArray.CreateBuilder<GrainDirectoryReplica>(DirectoryMembershipSnapshot.PartitionsPerSilo);
        for (var i = 0; i < DirectoryMembershipSnapshot.PartitionsPerSilo; i++)
        {
            partitions.Add(new GrainDirectoryReplica(i, this, localSiloDetails, loggerFactory, serviceProvider, grainFactory));
        }

        _partitions = partitions.ToImmutable();
    }

    public async Task<GrainAddress?> Lookup(GrainId grainId) => await InvokeAsync(
        grainId,
        static (replica, version, grainId, cancellationToken) => replica.LookupAsync(version, grainId),
        grainId,
        CancellationToken.None);

    public async Task<GrainAddress?> Register(GrainAddress address) => await InvokeAsync(
        address.GrainId,
        static (replica, version, address, cancellationToken) => replica.RegisterAsync(version, address, null),
        address,
        CancellationToken.None);

    public async Task Unregister(GrainAddress address) => await InvokeAsync(
        address.GrainId,
        static (replica, version, address, cancellationToken) => replica.DeregisterAsync(version, address),
        address,
        CancellationToken.None);

    public async Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousAddress) => await InvokeAsync(
        address.GrainId,
        static (replica, version, state, cancellationToken) => replica.RegisterAsync(version, state.Address, state.PreviousAddress),
        (Address: address, PreviousAddress: previousAddress),
        CancellationToken.None);

    public Task UnregisterSilos(List<SiloAddress> siloAddresses) => Task.CompletedTask;

    private async Task<TResult> InvokeAsync<TState, TResult>(
        GrainId grainId,
        Func<IGrainDirectoryPartition, MembershipVersion, TState, CancellationToken, ValueTask<DirectoryResult<TResult>>> func,
        TState state,
        CancellationToken cancellationToken,
        [CallerMemberName] string operation = "")
    {
        DirectoryResult<TResult> invokeResult;
        var view = _membershipService.CurrentView;
        var attempts = 0;
        const int MaxAttempts = 10;
        var delay = TimeSpan.FromMilliseconds(10);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var initialRecoveryMembershipVersion = _recoveryMembershipVersion;
            if (view.Version.Value < initialRecoveryMembershipVersion || !view.TryGetOwner(grainId, out var owner, out var partitionReference))
            {
                // If there are no members, bail out with the default return value.
                if (view.Members.Length == 0 && view.Version.Value > 0)
                {
                    return default!;
                }

                var targetVersion = Math.Max(view.Version.Value + 1, initialRecoveryMembershipVersion);
                view = await _membershipService.RefreshViewAsync(new(targetVersion), cancellationToken);
                continue;
            }

#if false
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace("Invoking '{Operation}' on '{Owner}' for grain '{GrainId}'.", operation, owner, grainId);
            }
#endif

            try
            {
                RequestContext.Set("gid", partitionReference.GetGrainId());
                invokeResult = await func(partitionReference, view.Version, state, cancellationToken);
            }
            catch (OrleansMessageRejectionException) when (attempts < MaxAttempts && !cancellationToken.IsCancellationRequested)
            {
                // This likely indicates that the target silo has been declared dead.
                ++attempts;
                await Task.Delay(delay);
                delay *= 1.5;
                continue;
            }

            if (initialRecoveryMembershipVersion != _recoveryMembershipVersion)
            {
                // If the recovery version changed, perform a view refresh and re-issue the operation.
                // See the comment on the declaration of '_recoveryMembershipVersionValue' for more details.
                continue;
            }

            if (!invokeResult.TryGetResult(view.Version, out var result))
            {
                // The remote replica has a newer view of membership and is no longer the owner of the grain specified in the request.
                // Refresh membership and re-evaluate.
                view = await _membershipService.RefreshViewAsync(invokeResult.Version, cancellationToken);
                continue;
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Invoked '{Operation}' on '{Owner}' for grain '{GrainId}' and received result '{Result}'.", operation, owner, grainId, result);
            }

            return result;
        }
    }

    public async ValueTask<Immutable<List<GrainAddress>>> GetRegisteredActivations(MembershipVersion membershipVersion, RingRange range, bool isValidation)
    {
        if (!isValidation && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Collecting registered activations for range {Range} at version {MembershipVersion}.", range, membershipVersion);
        }

        var recoveryMembershipVersion = _recoveryMembershipVersion;
        if (recoveryMembershipVersion < membershipVersion.Value)
        {
            // Ensure that the value is immediately visible to any thread registering an activation.
            Interlocked.CompareExchange(ref _recoveryMembershipVersion, membershipVersion.Value, recoveryMembershipVersion);
        }

        var localActivations = _serviceProvider.GetRequiredService<ActivationDirectory>();
        var grainDirectoryResolver = _serviceProvider.GetRequiredService<GrainDirectoryResolver>();
        List<GrainAddress> result = [];
        List<Task> deactivationTasks = [];
        var stopwatch = CoarseStopwatch.StartNew();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        foreach (var (grainId, activation) in localActivations)
        {
            var directory = GetGrainDirectory(activation, grainDirectoryResolver);
            if (directory is not null && directory == this)
            {
                var address = activation.Address;
                if (!range.Contains(address.GrainId))
                {
                    continue;
                }

                if (address.MembershipVersion == MembershipVersion.MinValue
                    || activation is ActivationData activationData && !activationData.IsValid)
                {
                    // Validation does not require that the grain is deactivated, skip it instead.
                    //if (isValidation) continue;

                    try
                    {
                        // This activation has not completed registration or is not currently active.
                        // Abort the activation with a pre-canceled cancellation token so that it skips directory deregistration.
                        // TODO: Expand validity check to non-ActivationData activations.
                        //logger.LogWarning("Deactivating activation '{Activation}' due to failure of a directory range owner.", activation);
                        activation.Deactivate(new DeactivationReason(DeactivationReasonCode.DirectoryFailure, "This activation's directory partition was salvaged while registration status was in-doubt."), cts.Token);
                        deactivationTasks.Add(activation.Deactivated);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogWarning(exception, "Failed to deactivate activation {Activation}", activation);
                    }
                }
                else
                {
                    if (!isValidation)
                    {
                        _logger.LogTrace("Sending activation '{Activation}' for recovery because its in the requested range {Range} (version {Version}).", activation.GrainId, range, membershipVersion);
                    }

                    result.Add(activation.Address);
                }
            }
        }

        await Task.WhenAll(deactivationTasks);

        if (!isValidation && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Submitting {Count} registered activations for range {Range} at version {MembershipVersion}. Deactivated {DeactivationCount} in-doubt registrations. Took {ElapsedMilliseconds}ms",
                result.Count,
                range,
                membershipVersion,
                deactivationTasks.Count,
                stopwatch.ElapsedMilliseconds);
        }

        return result.AsImmutable();

        static IGrainDirectory? GetGrainDirectory(IGrainContext grainContext, GrainDirectoryResolver grainDirectoryResolver)
        {
            if (grainContext is ActivationData activationData)
            {
                return activationData.Shared.GrainDirectory;
            }
            else if (grainContext is SystemTarget systemTarget)
            {
                return null;
            }
            else if (grainContext.GetComponent<PlacementStrategy>() is { IsUsingGrainDirectory: true })
            {
                return grainDirectoryResolver.Resolve(grainContext.GrainId.Type);
            }

            return null;
        }
    }

    internal ValueTask<DirectoryMembershipSnapshot> RefreshViewAsync(MembershipVersion version, CancellationToken cancellationToken) => _membershipService.RefreshViewAsync(version, cancellationToken);

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(nameof(DistributedGrainDirectory), ServiceLifecycleStage.RuntimeInitialize, OnRuntimeInitializeStart, OnRuntimeInitializeStop);

        // Transition into 'ShuttingDown'/'Stopping' stage, removing ourselves from directory membership, but allow some time for hand-off before transitioning to 'Dead'.
        observer.Subscribe(nameof(DistributedGrainDirectory), ServiceLifecycleStage.BecomeActive - 1, _ => Task.CompletedTask, OnShuttingDown);

        Task OnRuntimeInitializeStart(CancellationToken cancellationToken)
        {
            var catalog = _serviceProvider.GetRequiredService<Catalog>();
            catalog.RegisterSystemTarget(this);
            foreach (var partition in _partitions)
            {
                catalog.RegisterSystemTarget(partition);
            }

            using var _ = new ExecutionContextSuppressor();
            WorkItemGroup.QueueAction(() => _runTask = ProcessMembershipUpdates());

            return Task.CompletedTask;
        }

        async Task OnRuntimeInitializeStop(CancellationToken cancellationToken)
        {
            _stoppedCts.Cancel();
            if (_runTask is { } task)
            {
                // Try to wait for hand-off to complete.
                await this.RunOrQueueTask(async () => await task.WaitAsync(cancellationToken).SuppressThrowing());
            }
        }

        async Task OnShuttingDown(CancellationToken token)
        {
            var tasks = new List<Task>(_partitions.Length);
            foreach (var partition in _partitions)
            {
                tasks.Add(partition.OnShuttingDown(token));
            }
            await Task.WhenAll(tasks).SuppressThrowing();
        }
    }

    private async Task ProcessMembershipUpdates()
    {
        // Ensure all child tasks are completed before exiting, tracking them here.
        List<Task> tasks = [];
        var previousUpdate = ClusterMembershipSnapshot.Default;
        while (!_stoppedCts.IsCancellationRequested)
        {
            try
            {
                await foreach (var update in _membershipService.ViewUpdates.WithCancellation(_stoppedCts.Token))
                {
                    tasks.RemoveAll(t => t.IsCompleted);
                    var changes = update.ClusterMembershipSnapshot.CreateUpdate(previousUpdate);

                    foreach (var change in changes.Changes)
                    {
                        if (change.Status == SiloStatus.Dead)
                        {
                            foreach (var partition in _partitions)
                            {
                                tasks.Add(partition.OnSiloRemovedFromClusterAsync(change));
                            }
                        }
                    }

                    var current = update;

                    foreach (var partition in _partitions)
                    {
                        tasks.Add(partition.ProcessMembershipUpdateAsync(current));
                    }

                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("Updated view from '{PreviousVersion}' to '{Version}'.", previousUpdate.Version, update.Version);
                    }

                    previousUpdate = update.ClusterMembershipSnapshot;
                }
            }
            catch (Exception exception)
            {
                if (!_stoppedCts.IsCancellationRequested)
                {
                    _logger.LogError(exception, "Error processing membership updates.");
                }
            }
        }

        await Task.WhenAll(tasks).SuppressThrowing();
    }

    SiloAddress? ITestHooks.GetPrimaryForGrain(GrainId grainId)
    {
        _membershipService.CurrentView.TryGetOwner(grainId, out var owner, out _);
        return owner;
    }

    async Task<GrainAddress?> ITestHooks.GetLocalRecord(GrainId grainId)
    {
        var view = _membershipService.CurrentView;
        if (view.TryGetOwner(grainId, out var owner, out var partitionReference) && Silo.Equals(owner))
        {
            var result = await partitionReference.LookupAsync(view.Version, grainId);
            if (result.TryGetResult(view.Version, out var address))
            {
                return address;
            }
        }

        return null;
    }

    internal interface ITestHooks
    {
        SiloAddress? GetPrimaryForGrain(GrainId grainId);
        Task<GrainAddress?> GetLocalRecord(GrainId grainId);
    }
}
