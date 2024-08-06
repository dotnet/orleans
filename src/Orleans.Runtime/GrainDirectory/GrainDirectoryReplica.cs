using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Internal;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.Utilities;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

internal sealed partial class GrainDirectoryReplica(
    ILocalSiloDetails localSiloDetails,
    ClusterMembershipService clusterMembershipService,
    ILoggerFactory loggerFactory,
    IServiceProvider serviceProvider,
    IInternalGrainFactory grainFactory)
    : SystemTarget(Constants.DirectoryReplicaType, localSiloDetails.SiloAddress, loggerFactory), IGrainDirectoryReplica, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly Dictionary<GrainId, GrainAddress> _directory = [];
    private readonly ClusterMembershipService _clusterMembershipService = clusterMembershipService;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IInternalGrainFactory _grainFactory = grainFactory;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly SiloAddress _id = localSiloDetails.SiloAddress;
    private readonly ILogger<GrainDirectoryReplica> _logger = loggerFactory.CreateLogger<GrainDirectoryReplica>();
    private readonly TaskCompletionSource _shutdownTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AsyncEnumerable<DirectoryMembershipSnapshot> _viewUpdates = new(
        DirectoryMembershipSnapshot.Default,
        (previous, proposed) => proposed.Version >= previous.Version,
        _ => { });

    // Ranges which cannot be served currently, eg because the replica is currently transferring them from a previous owner.
    // Requests in these ranges must wait for the range to become available.
    private readonly List<(RingRange Range, MembershipVersion Version, TaskCompletionSource Completion)> _pendingRanges = [];

    // Ranges which were previously at least partially owned by this replica, but which are pending transfer to a new replica.  
    private readonly List<PartitionSnapshotState> _partitionSnapshots = [];

    // The current directory membership snapshot.
    private DirectoryMembershipSnapshot _view = DirectoryMembershipSnapshot.Default;

    private Task? _runTask;

    public DirectoryMembershipSnapshot CurrentView => _view;

    public IAsyncEnumerable<DirectoryMembershipSnapshot> ViewUpdates => _viewUpdates;

    public async ValueTask<DirectoryMembershipSnapshot> RefreshViewAsync(MembershipVersion version, CancellationToken cancellationToken)
    {
        var stopwatch = ValueStopwatch.StartNew();
        _ = _clusterMembershipService.Refresh(version, cancellationToken);
        if (_view.Version <= version)
        {
            await foreach (var view in _viewUpdates.WithCancellation(cancellationToken))
            {
                if (view.Version >= version)
                {
                    break;
                }
            }

            if (_logger.IsEnabled(LogLevel.Information) && stopwatch.Elapsed.TotalMilliseconds > 50)
            {
                _logger.LogInformation("Refreshed view to version '{Version}' in {Elapsed}ms.", version, stopwatch.Elapsed.TotalMilliseconds);
            }
        }

        return _view;
    }

    async ValueTask<GrainDirectoryPartitionSnapshot?> IGrainDirectoryReplica.GetPartitionSnapshotAsync(MembershipVersion version, MembershipVersion rangeVersion, RingRangeCollection ranges)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("GetPartitionSnapshotAsync('{Version}', '{RangeVersion}', '{Range}')", version, rangeVersion, ranges);
        }

        // Wait for the range to be un-wedged.
        await RefreshViewAsync(version, CancellationToken.None);
        foreach (var range in ranges)
        {
            var stopwatch = CoarseStopwatch.StartNew();
            await WaitForRange(range, rangeVersion, CancellationToken.None);
            if (stopwatch.Elapsed.TotalMilliseconds > 500)
            {
                _logger.LogInformation("Waited for range '{Range}' at version '{Version}' for {Elapsed}ms.", range, rangeVersion, stopwatch.ElapsedMilliseconds);
            }
        }

        List<GrainAddress> partitionAddresses = [];
        foreach (var partitionSnapshot in _partitionSnapshots)
        {
            if (partitionSnapshot.DirectoryMembershipVersion != rangeVersion)
            {
                continue;
            }

            // Only include addresses which are in the requested range.
            foreach (var address in partitionSnapshot.GrainAddresses)
            {
                foreach (var range in ranges)
                {
                    if (range.Contains(address.GrainId))
                    {
                        partitionAddresses.Add(address);
                        break;
                    }
                }
            }

            var rangeSnapshot = new GrainDirectoryPartitionSnapshot(rangeVersion, partitionAddresses);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Transferring '{Count}' entries in range '{Range}' from version '{Version}' snapshot.", partitionAddresses.Count, ranges, rangeVersion);
            }

            return rangeSnapshot;
        }

        _logger.LogWarning("Received a request for a snapshot which this replica does not have, version '{Version}', range version '{RangeVersion}', range '{Range}'.", version, rangeVersion, ranges);
        return null;
    }

    ValueTask<bool> IGrainDirectoryReplica.AcknowledgeSnapshotTransferAsync(SiloAddress owner, MembershipVersion rangeVersion)
    {
        RemoveSnapshotTransferPartner(owner, rangeVersion);
        return new (true);
    }

    private void RemoveSnapshotTransferPartner(SiloAddress owner, MembershipVersion? rangeVersion)
    {
        for (var i = 0; i < _partitionSnapshots.Count; ++i)
        {
            var partitionSnapshot = _partitionSnapshots[i];
            if (rangeVersion.HasValue && partitionSnapshot.DirectoryMembershipVersion != rangeVersion.Value)
            {
                continue;
            }

            var partners = partitionSnapshot.TransferPartners;
            if (partners.Remove(owner) && partners.Count == 0)
            {
                _partitionSnapshots.RemoveAt(i);

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Removing version '{Version}' snapshot. Current snapshots: [{CurrentSnapshots}].", partitionSnapshot.DirectoryMembershipVersion, string.Join(", ", _partitionSnapshots.Select(s => s.DirectoryMembershipVersion)));
                }

                // If shutdown has been requested and there are no more pending snapshots, signal completion.
                if (_shutdownCts.IsCancellationRequested && _partitionSnapshots.Count == 0)
                {
                    _shutdownTcs.TrySetResult();
                }
            }
        }
    }

    [Conditional("DEBUG")]
    private void AssertOwnership(GrainId grainId) => DebugAssertOwnership(_view, grainId);

    [Conditional("DEBUG")]
    private void DebugAssertOwnership(DirectoryMembershipSnapshot view, GrainId grainId)
    {
        if (!view.TryGetOwner(grainId, out var owner))
        {
            Debugger.Launch();
            Debug.Fail($"Could not find owner for grain grain '{grainId}' in view '{view}'.");
        }

        if (!_id.Equals(owner))
        {
            Debugger.Launch();
            Debug.Fail($"'{_id}' expected to be the owner of grain '{grainId}', but the owner is '{owner}'.");
        }
    }

    private ValueTask WaitForRange(GrainId grainId, MembershipVersion version, CancellationToken cancellationToken) => WaitForRange(RingRange.FromPoint(grainId.GetUniformHashCode()), version, cancellationToken);

    private async ValueTask WaitForRange(RingRange ranges, MembershipVersion version, CancellationToken cancellationToken)
    {
        if (_view.Version < version)
        {
            await RefreshViewAsync(version, cancellationToken);
        }

        while (TryGetOverlappingWedge(ranges, version, out var completion))
        {
            await completion.WaitAsync(cancellationToken);
        }

        bool TryGetOverlappingWedge(RingRange ranges, MembershipVersion version, [NotNullWhen(true)] out Task? completion)
        {
            foreach (var wedge in _pendingRanges)
            {
                if (wedge.Version <= version && ranges.Intersects(wedge.Range))
                {
                    completion = wedge.Completion.Task;
                    return true;
                }
            }

            completion = null;
            return false;
        }
    }

    public IGrainDirectoryReplica GetReplica(SiloAddress address) => _grainFactory.GetSystemTarget<IGrainDirectoryReplica>(Constants.DirectoryReplicaType, address);

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(nameof(GrainDirectoryReplica), ServiceLifecycleStage.RuntimeInitialize, OnRuntimeInitializeStart, OnRuntimeInitializeStop);

        // Transition into 'ShuttingDown'/'Stopping' stage, removing ourselves from directory membership, but allow some time for hand-off before transitioning to 'Dead'.
        observer.Subscribe(nameof(GrainDirectoryReplica), ServiceLifecycleStage.BecomeActive - 1, _ => Task.CompletedTask, OnShuttingDown);
    } 

    private async Task OnShuttingDown(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _partitionSnapshots.Count > 0)
        {
            await _shutdownTcs.Task.WaitAsync(token).SuppressThrowing();
        }
    }

    private Task OnRuntimeInitializeStart(CancellationToken cancellationToken)
    {
        var catalog = _serviceProvider.GetRequiredService<Catalog>();
        catalog.RegisterSystemTarget(this);

        using var _ = new ExecutionContextSuppressor();
        WorkItemGroup.QueueAction(() => _runTask = ProcessMembershipUpdates());

        return Task.CompletedTask;
    }

    async Task OnRuntimeInitializeStop(CancellationToken cancellationToken)
    {
        _shutdownCts.Cancel();
        if (_runTask is { } task)
        {
            // Try to wait for hand-off to complete.
            await this.RunOrQueueTask(async () => await task.WaitAsync(cancellationToken).SuppressThrowing());
        }
    }

    private async Task ProcessMembershipUpdates()
    {
        try
        {
            // Ensure all child tasks are completed before exiting, tracking them here.
            List<Task> tasks = [];
            var previousUpdate = ClusterMembershipSnapshot.Default;
            while (!_shutdownCts.IsCancellationRequested)
            {
                try
                {
                    var previousRanges = _view.GetRanges(_id);
                    await foreach (var update in _clusterMembershipService.MembershipUpdates.WithCancellation(_shutdownCts.Token))
                    {
                        var changes = update.CreateUpdate(previousUpdate);
                        
                        foreach (var change in changes.Changes)
                        {
                            if (change.Status == SiloStatus.Dead)
                            {
                                OnSiloRemovedFromCluster(change);
                            }
                        }

                        var current = new DirectoryMembershipSnapshot(update);

                        // It is important that this method is synchronous, to ensure that updates are atomic.
                        var currentRanges = current.GetRanges(_id);
                        var deltaSize = currentRanges.SizePercent - previousRanges.SizePercent;
                        var meanSizePercent = current.Members.Length > 0 ? 100.0 / current.Members.Length : 0f;
                        var deviationFromMean = Math.Abs(meanSizePercent - currentRanges.SizePercent);
                        _logger.LogInformation("Updating view from '{PreviousVersion}' to '{Version}'. Now responsible for '{Range}' (Δ {DeltaPercent:0.00}%. {DeviationFromMean:0.00}% from ideal share).", previousUpdate.Version, update.Version, currentRanges, deltaSize, deviationFromMean);
                        ProcessMembershipUpdate(tasks, current);
                        tasks.RemoveAll(task => task.IsCompleted);

                        _logger.LogInformation("Updated view from '{PreviousVersion}' to '{Version}'.", previousUpdate.Version, update.Version);
                        _viewUpdates.Publish(current);
                        previousUpdate = update;
                        previousRanges = currentRanges;
                    }
                }
                catch (Exception exception)
                {
                    if (!_shutdownCts.IsCancellationRequested)
                    {
                        _logger.LogError(exception, "Error processing membership updates.");
                    }
                }
            }

            await Task.WhenAll(tasks).SuppressThrowing();
        }
        finally
        {
            _viewUpdates.Dispose();
        }
    }

    private void OnSiloRemovedFromCluster(ClusterMember change)
    {
        var toRemove = new List<GrainAddress>();
        foreach (var entry in _directory)
        {
            if (change.SiloAddress.Equals(entry.Value.SiloAddress))
            {
                toRemove.Add(entry.Value);
            }
        }

        if (toRemove.Count > 0)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Deleting '{Count}' entries located on now-defunct silo '{SiloAddress}'.", toRemove.Count, change.SiloAddress);
            }

            foreach (var grainAddress in toRemove)
            {
                UnregisterCore(grainAddress);
            }
        }

        RemoveSnapshotTransferPartner(change.SiloAddress, rangeVersion: null);
    }

    private void ProcessMembershipUpdate(List<Task> tasks, DirectoryMembershipSnapshot current)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("Observed membership version '{Version}'.", current.Version);
        }

        var previous = _view;
        _view = current;

        var previousRanges = previous.GetRanges(_id);
        var currentRanges = current.GetRanges(_id);

        // Snapshot & remove everything not in the current range.
        // The new owner will have the opportunity to retrieve the snapshot as they take ownership.
        List<GrainAddress> removedAddresses = [];
        HashSet<SiloAddress> transferPartners = [];
        foreach (var removedRange in currentRanges.GetRemovals(previousRanges))
        {
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Relinquishing ownership of range '{Range}'.", removedRange);
            }

            foreach (var (range, ownerIndex) in current.RangeOwners)
            {
                if (range.Intersects(removedRange))
                {
                    var owner = current.Members[ownerIndex];
                    Debug.Assert(!_id.Equals(owner));
                    transferPartners.Add(owner);
                }
            }

            // Collect all addresses that are not in the owned range.
            foreach (var entry in _directory)
            {
                if (removedRange.Contains(entry.Key))
                {
                    removedAddresses.Add(entry.Value);
                }
            }

            // Remove these addresses from the partition.
            foreach (var address in removedAddresses)
            {
                _directory.Remove(address.GrainId);
            }
        }

        if (transferPartners.Count > 0)
        {
            _partitionSnapshots.Add(new PartitionSnapshotState(previous.Version, removedAddresses, transferPartners));
        }

        var addedRanges = currentRanges.GetAdditions(previousRanges);
        if (!addedRanges.IsEmpty)
        {
            tasks.Add(TransferOwnershipAsync(previous, current, addedRanges));
        }
    }

    private async Task TransferOwnershipAsync(DirectoryMembershipSnapshot previous, DirectoryMembershipSnapshot current, RingRangeCollection addedRanges)
    {
        var stopwatch = CoarseStopwatch.StartNew();

        // The view change is contiguous if the new version is exactly one greater than the previous version.
        // If not, we have missed some updates, so we must declare a potential data loss event.
        var isContiguous = current.Version.Value == previous.Version.Value + 1;
        bool success;
        if (isContiguous)
        {
            // Transfer subranges from previous owners.
            var tasks = new List<Task<bool>>();
            foreach (var previousOwner in previous.Members)
            {
                var previousOwnerRanges = previous.GetRanges(previousOwner);
                if (addedRanges.Overlaps(previousOwnerRanges))
                {
                    tasks.Add(TransferRangeAsync(current, addedRanges, previousOwner, previous.Version));
                }
            }

            // Note: there should be no 'await' points before this point.
            // An await before this point would result in ranges not being wedged synchronously.
            await Task.WhenAll(tasks).WaitAsync(_shutdownCts.Token).SuppressThrowing();
            if (_shutdownCts.IsCancellationRequested)
            {
                return;
            }

            success = tasks.All(t => t.Result);
        }
        else
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Non-contiguous view change detected: '{PreviousVersion}' to '{CurrentVersion}'. Performing recovery.",
                    previous.Version,
                    current.Version);
            }

            // Suspend all ranges.
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (var addedRange in addedRanges.Ranges)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("Accepting ownership of range '{Range}'.", addedRange);
                }

                _pendingRanges.Add((addedRange, current.Version, tcs));
            }

            success = false;
        }

        var recovered = false;
        if (!success)
        {
            await RecoverPartitionRange(current, addedRanges);
            ResumeAllRanges(current.Version);
            recovered = true;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Completed transferring entries for range '{Range}' at version '{Version}' took {Elapsed}ms.{Recovered}", addedRanges, current.Version, stopwatch.ElapsedMilliseconds, recovered ? " Recovered" : "");
        }

        void ResumeAllRanges(MembershipVersion currentVersion)
        {
            // Resume any remaining ranges for this version.
            foreach (var pending in _pendingRanges)
            {
                if (pending.Version == currentVersion)
                {
                    pending.Completion.TrySetResult();
                }
            }

            _pendingRanges.RemoveAll(p => p.Version == currentVersion);
        }
    }

    private async Task<bool> TransferRangeAsync(DirectoryMembershipSnapshot current, RingRangeCollection addedRanges, SiloAddress previousOwner, MembershipVersion previousVersion)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var addedRange in addedRanges.Ranges)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Accepting ownership of range '{Range}'.", addedRange);
            }

            // Suspend this range and transfer state from the previous owner.
            // If the predecessor becomes unavailable or membership advances quickly, we will declare data loss and un-wedge the range.
            _pendingRanges.Add((addedRange, current.Version, tcs));
        }

        try
        {
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Requesting entries for ranges '{Range}' from '{PreviousOwner}' at version '{PreviousVersion}'.", addedRanges, previousOwner, previousVersion);
            }

            var replica = GetReplica(previousOwner);

            // Alternatively, the previous owner could push the snapshot. The pull-based approach is used here because it is simpler.
            var snapshot = await replica.GetPartitionSnapshotAsync(current.Version, previousVersion, addedRanges).AsTask().WaitAsync(_shutdownCts.Token);

            if (snapshot is null)
            {
                _logger.LogWarning("Expected a valid snapshot from previous owner '{PreviousOwner}' for part of ranges '{Range}', but found none.", previousOwner, addedRanges);
                return false;
            }

            // The acknowledgement step lets the previous owner know that the snapshot has been received so that it can proceed.
            var ackTask = InvokeOnClusterMember(
                previousOwner,
                async () => await replica.AcknowledgeSnapshotTransferAsync(_id, previousVersion),
                false,
                nameof(IGrainDirectoryReplica.AcknowledgeSnapshotTransferAsync));

            // Wait for previous versions to be un-wedged before proceeding.
            foreach (var range in addedRanges)
            {
                await WaitForRange(range, previousVersion, CancellationToken.None);
            }

            // Incorporate the values into the grain directory.
            foreach (var entry in snapshot.GrainAddresses)
            {
                DebugAssertOwnership(current, entry.GrainId);
                _directory[entry.GrainId] = entry;
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Transferred '{Count}' entries for range '{Range}' from '{PreviousOwner}'.", snapshot.GrainAddresses.Count, addedRanges, previousOwner);
            }

            // Resume the suspended ranges.
            tcs.SetResult();
            foreach (var addedRange in addedRanges.Ranges)
            {
                _pendingRanges.Remove((addedRange, current.Version, tcs));
            }

            await ackTask;
            return true;
        }
        catch (Exception exception)
        {
            if (exception is SiloUnavailableException)
            {
                _logger.LogWarning("Remote host became unavailable while transferring ownership of range '{Range}'. Recovery will be performed.", addedRanges);
            }
            else
            {
                _logger.LogWarning(exception, "Error transferring ownership of range '{Range}'. Recovery will be performed.", addedRanges);
            }

            return false;
        }
    }

    private async Task RecoverPartitionRange(DirectoryMembershipSnapshot current, RingRangeCollection addedRanges)
    {
        _logger.LogInformation("Recovering activations from ranges '{Range}' at version '{Version}'.", addedRanges, current.Version);
        var tasks = new List<Task<List<GrainAddress>>>();

        // Membership is guaranteed to be newer than the current view.
        var clusterMembershipSnapshot = _clusterMembershipService.CurrentSnapshot;
        Debug.Assert(clusterMembershipSnapshot.Version >= current.Version);
        var members = new List<ClusterMember>();
        foreach (var member in clusterMembershipSnapshot.Members.Values)
        {
            if (member.Status is not (SiloStatus.Active or SiloStatus.Joining or SiloStatus.ShuttingDown))
            {
                continue;
            }

            members.Add(member);
            tasks.Add(GetRegisteredActivations(current.Version, addedRanges, member.SiloAddress));
        }

        await Task.WhenAll(tasks).WaitAsync(_shutdownCts.Token).SuppressThrowing();
        if (_shutdownCts.IsCancellationRequested)
        {
            return;
        }

        for (var i = 0; i < tasks.Count; ++i)
        {
            var activations = await tasks[i];
            foreach (var entry in activations)
            {
                DebugAssertOwnership(current, entry.GrainId);
                _directory[entry.GrainId] = entry;
            }
        }

        async Task<List<GrainAddress>> GetRegisteredActivations(MembershipVersion version, RingRangeCollection ranges, SiloAddress siloAddress)
        {
            var stopwatch = ValueStopwatch.StartNew();
            var client = _grainFactory.GetSystemTarget<IGrainDirectoryReplicaClient>(Constants.DirectoryReplicaClientType, siloAddress);
            var result = await InvokeOnClusterMember(
                siloAddress,
                async () => await client.GetRegisteredActivations(version, ranges),
                new Immutable<List<GrainAddress>>([]),
                nameof(GetRegisteredActivations));

            if (_logger.IsEnabled(LogLevel.Information) && stopwatch.Elapsed.TotalMilliseconds > 50)
            {
                _logger.LogInformation("Recovered '{Count}' entries from silo '{SiloAddress}' for ranges '{Range}' at version '{Version}' in {ElapsedMilliseconds}ms.", result.Value.Count, siloAddress, ranges, version, stopwatch.Elapsed.TotalMilliseconds);
            }

            return result.Value;
        }
    }

    private async Task<T> InvokeOnClusterMember<T>(SiloAddress siloAddress, Func<Task<T>> func, T defaultValue, string operationName)
    {
        var clusterMembershipSnapshot = _clusterMembershipService.CurrentSnapshot;
        while (!_shutdownCts.IsCancellationRequested)
        {
            if (clusterMembershipSnapshot.GetSiloStatus(siloAddress) is not (SiloStatus.Active or SiloStatus.Joining or SiloStatus.ShuttingDown))
            {
                break;
            }

            try
            {
                return await func();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error invoking operation '{Operation}' on silo '{SiloAddress}'.", operationName, siloAddress);
                await _clusterMembershipService.Refresh(default, CancellationToken.None);
                if (_clusterMembershipService.CurrentSnapshot.Version == clusterMembershipSnapshot.Version)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100));
                }

                clusterMembershipSnapshot = _clusterMembershipService.CurrentSnapshot;
            }
        }

        return defaultValue;
    }

    private sealed record class PartitionSnapshotState(
        MembershipVersion DirectoryMembershipVersion,
        List<GrainAddress> GrainAddresses,
        HashSet<SiloAddress> TransferPartners);
}
