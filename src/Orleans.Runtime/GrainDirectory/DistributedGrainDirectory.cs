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

/*
The grain directory in Orleans is a key-value store where the key is a grain identifier and the value is a registration entry which points to an active silo which (potentially)
hosts the grain.

The directory is partitioned using a consistent hash ring with ranges being assigned to the active silos in the cluster. Grain identifiers are hashed to find the silo which is
owns the section of the ring corresponding to its hash. Each active silo owns a pre-configured number of ranges, defaulting to 30 ranges per silo. This is similar to the scheme
used by Amazon Dynamo (see https://www.allthingsdistributed.com/files/amazon-dynamo-sosp2007.pdf) and Apache Cassandra (see
https://docs.datastax.com/en/cassandra-oss/3.0/cassandra/architecture/archDataDistributeVnodesUsing.html), where multiple "virtual nodes" (ranges) are created for each node
(host). The size of a partition is determined by the distance between its hash and the hash of the next partition. Range ownership is determined by cluster membership
configuration. Cluster membership configurations are called "views" and each view has a monotonically increasing version number. As silos join and leave the cluster, successive
views are created, resulting in changes to range ownership. This is known as a view change. Directory partitions have two modes of operation: normal operation and view change.
During normal operation, directory partitions process requests locally without coordination with other hosts. During a view changes, hosts coordinate with each other to transfer
ownership of directory ranges. Once this transfer is complete, normal operation resumes. The two-phase design of the directory follows the Virtual Synchrony methodology (see
https://www.microsoft.com/en-us/research/publication/virtually-synchronous-methodology-for-dynamic-service-replication/) and has some similarities to Vertical Paxos (see
https://www.microsoft.com/en-us/research/publication/vertical-paxos-and-primary-backup-replication/). Both proceed in two phases: a normal operation phase where a fixed set of
processes handle requests without failures, and a view change phase where state and control are transferred between views during membership changes.

When a view change occurs, a partition can either grow or shrink. If a new silo has joined the cluster, then the partition may shrink to make room for the new silo's partition. If
a silo has left the cluster, then the partition may grow to take over the leaving silo's partition. When a partition shrinks, the previous owner seals the lost range and creates a
snapshot containing the directory entries in that range. The new range owner (whose partition has grown, potentially from zero) requests the snapshot from the previous owner and
applies it locally. Once the snapshot has been applied, the new owner can begin servicing requests. The new owner acknowledges the transfer to the previous owner so the previous
owner can delete the snapshot. The previous owner also deletes the snapshot if it sees that the snapshot transfer has been abandoned.

When a host crashes without first handing off its directory partitions, the hosts which subsequently own the partitions previously owned by the crashed silo must perform recovery.
Recovery involves asking every active silo in the cluster for the grain registrations they own. Registrations for evicted silos do not need to be recovered, since registrations are
only valid for active silos. The recovery procedure ensures that there is no data loss and that the directory remains consistent (no duplicate grain activations).

Cluster membership guarantees monotonicity, but it does not guarantee that all silos see all membership views: it is possible for silos to skip intermediate membership view, for
example if membership changes rapidly. When this happens, snapshot transfers are abandoned and recovery must be performed instead of the normal partition-to-partition hand-over,
since the silo does not know with certainty which partition was the previous owner. A future improvement to cluster membership may reduce or eliminate this scenario by ensuring
that all views are seen by all silos.

Directory partitions (implemented in GrainDirectoryPartition) use versioned range locks to prevent invalid access to ranges during view changes. Range locks are created during
view change and are released when the view change is complete. These locks are analogous to the 'wedges' used in the Virtual Synchrony methodology. It is possible for a range to
be split among multiple silos during a view change. This adds some complexity to the view change procedure since each partition must potentially coordinate with multiple other
partitions to complete the view change.

All requests to a directory partition include the view number of the caller, and all responses from the directory include the view number of the directory partition. When the
directory partition sees a view number higher than its own, it refreshes its view and initiates view change. Similarly, when a caller sees a response with a higher view number
than its own, it refreshes its view and retries the request if necessary. This ensures that all requests are processed by the correct owner of the directory partition.
*/
internal sealed partial class DistributedGrainDirectory : SystemTarget, IGrainDirectory, IGrainDirectoryClient, ILifecycleParticipant<ISiloLifecycle>, DistributedGrainDirectory.ITestHooks
{
    private readonly DirectoryMembershipService _membershipService;
    private readonly ILogger<DistributedGrainDirectory> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ImmutableArray<GrainDirectoryPartition> _partitions;
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
        var partitions = ImmutableArray.CreateBuilder<GrainDirectoryPartition>(DirectoryMembershipSnapshot.PartitionsPerSilo);
        for (var i = 0; i < DirectoryMembershipSnapshot.PartitionsPerSilo; i++)
        {
            partitions.Add(new GrainDirectoryPartition(i, this, localSiloDetails, loggerFactory, serviceProvider, grainFactory));
        }

        _partitions = partitions.ToImmutable();
    }

    public async Task<GrainAddress?> Lookup(GrainId grainId) => await InvokeAsync(
        grainId,
        static (partition, version, grainId, cancellationToken) => partition.LookupAsync(version, grainId),
        grainId,
        CancellationToken.None);

    public async Task<GrainAddress?> Register(GrainAddress address) => await InvokeAsync(
        address.GrainId,
        static (partition, version, address, cancellationToken) => partition.RegisterAsync(version, address, null),
        address,
        CancellationToken.None);

    public async Task Unregister(GrainAddress address) => await InvokeAsync(
        address.GrainId,
        static (partition, version, address, cancellationToken) => partition.DeregisterAsync(version, address),
        address,
        CancellationToken.None);

    public async Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousAddress) => await InvokeAsync(
        address.GrainId,
        static (partition, version, state, cancellationToken) => partition.RegisterAsync(version, state.Address, state.PreviousAddress),
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

    public async ValueTask<Immutable<List<GrainAddress>>> RecoverRegisteredActivations(MembershipVersion membershipVersion, RingRange range, SiloAddress siloAddress, int partitionIndex)
    {
        foreach (var partition in _partitions)
        {
            partition.OnRecoveringPartition(membershipVersion, range, siloAddress, partitionIndex).Ignore();
        }

        return await GetRegisteredActivations(membershipVersion, range, false);
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
            if (directory == this)
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
                DirectoryMembershipSnapshot previous = _membershipService.CurrentView;
                var previousRanges = RingRangeCollection.Empty;
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
                    var currentRanges = current.GetMemberRanges(Silo);

                    foreach (var partition in _partitions)
                    {
                        tasks.Add(partition.ProcessMembershipUpdateAsync(current));
                    }

                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        var deltaSize = currentRanges.SizePercent - previousRanges.SizePercent;
                        var meanSizePercent = current.Members.Length > 0 ? 100.0 / current.Members.Length : 0f;
                        var deviationFromMean = Math.Abs(meanSizePercent - currentRanges.SizePercent);
                        _logger.LogDebug(
                            "Updated view from '{PreviousVersion}' to '{Version}'. Now responsible for {Range:0.00}% (Δ {DeltaPercent:0.00}%). {DeviationFromMean:0.00}% from ideal share.",
                             previous.Version,
                             current.Version,
                             currentRanges.SizePercent,
                             deltaSize,
                             deviationFromMean);
                    }

                    previousUpdate = update.ClusterMembershipSnapshot;
                    previous = current;
                    previousRanges = currentRanges;
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
