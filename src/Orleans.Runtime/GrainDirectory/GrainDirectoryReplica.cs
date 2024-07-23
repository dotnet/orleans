using System;
using System.Collections.Generic;
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
    : SystemTarget(Constants.DirectoryReplicaType, localSiloDetails.SiloAddress, loggerFactory), IGrainDirectoryReplica, IGrainDirectoryReplicaTestHooks, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly Dictionary<GrainId, GrainAddress> _directory = [];
    private readonly ClusterMembershipService _clusterMembershipService = clusterMembershipService;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IInternalGrainFactory _grainFactory = grainFactory;
    private readonly CancellationTokenSource _drainSnapshotsCts = new();
    private readonly CancellationTokenSource _stoppedCts = new();
    private readonly SiloAddress _id = localSiloDetails.SiloAddress;
    private readonly ILogger<GrainDirectoryReplica> _logger = loggerFactory.CreateLogger<GrainDirectoryReplica>();
    private readonly TaskCompletionSource _snapshotsDrainedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AsyncEnumerable<DirectoryMembershipSnapshot> _viewUpdates = new(
        DirectoryMembershipSnapshot.Default,
        (previous, proposed) => proposed.Version >= previous.Version,
        _ => { });

    // Ranges which cannot be served currently, eg because the replica is currently transferring them from a previous owner.
    // Requests in these ranges must wait for the range to become available.
    private readonly List<(RingRange Range, MembershipVersion Version, TaskCompletionSource Completion)> _rangeLocks = [];

    // Ranges which were previously at least partially owned by this replica, but which are pending transfer to a new replica.  
    private readonly List<PartitionSnapshotState> _partitionSnapshots = [];

    // The current directory membership snapshot.
    private DirectoryMembershipSnapshot _view = DirectoryMembershipSnapshot.Default;

    private Task? _runTask;

    public DirectoryMembershipSnapshot View => _view;

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
        }

        return _view;
    }

    async ValueTask<GrainDirectoryPartitionSnapshot?> IGrainDirectoryReplica.GetSnapshotAsync(MembershipVersion version, MembershipVersion rangeVersion, RingRangeCollection ranges)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("GetSnapshotAsync('{Version}', '{RangeVersion}', '{Range}')", version, rangeVersion, ranges);
        }

        // Wait for the range to be unlocked.
        foreach (var range in ranges)
        {
            var stopwatch = CoarseStopwatch.StartNew();
            await WaitForRange(range, version);
            if (stopwatch.Elapsed.TotalMilliseconds > 500)
            {
                _logger.LogDebug("Waited for range '{Range}' at version '{Version}' for {Elapsed}ms.", range, rangeVersion, stopwatch.ElapsedMilliseconds);
            }
        }

        _stoppedCts.Token.ThrowIfCancellationRequested();
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
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Transferring '{Count}' entries in range '{Range}' from version '{Version}' snapshot.", partitionAddresses.Count, ranges, rangeVersion);
            }

            return rangeSnapshot;
        }

        _logger.LogWarning("Received a request for a snapshot which this replica does not have, version '{Version}', range version '{RangeVersion}', range '{Range}'.", version, rangeVersion, ranges);
        return null;
    }

    ValueTask<bool> IGrainDirectoryReplica.AcknowledgeSnapshotTransferAsync(SiloAddress owner, MembershipVersion rangeVersion)
    {
        RemoveSnapshotTransferPartner(owner, rangeVersion);
        return new(true);
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
                --i;

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Removing version '{Version}' snapshot. Current snapshots: [{CurrentSnapshots}].", partitionSnapshot.DirectoryMembershipVersion, string.Join(", ", _partitionSnapshots.Select(s => s.DirectoryMembershipVersion)));
                }

                // If shutdown has been requested and there are no more pending snapshots, signal completion.
                if (_drainSnapshotsCts.IsCancellationRequested && _partitionSnapshots.Count == 0)
                {
                    _snapshotsDrainedTcs.TrySetResult();
                }
            }
        }
    }

    [Conditional("DEBUG")]
    private void DebugAssertOwnership(GrainId grainId) => DebugAssertOwnership(_view, grainId);

    [Conditional("DEBUG")]
    private void DebugAssertOwnership(DirectoryMembershipSnapshot view, GrainId grainId)
    {
        if (!view.TryGetOwner(grainId, out var owner))
        {
            Debug.Fail($"Could not find owner for grain grain '{grainId}' in view '{view}'.");
        }

        if (!_id.Equals(owner))
        {
            Debug.Fail($"'{_id}' expected to be the owner of grain '{grainId}', but the owner is '{owner}'.");
        }
    }

    private bool IsOwner(DirectoryMembershipSnapshot view, GrainId grainId) => view.TryGetOwner(grainId, out var owner) && _id.Equals(owner);

    private ValueTask WaitForRange(GrainId grainId, MembershipVersion version) => WaitForRange(RingRange.FromPoint(grainId.GetUniformHashCode()), version);

    private ValueTask WaitForRange(RingRange range, MembershipVersion version)
    {
        Task? completion = null;
        if (_view.Version < version || TryGetIntersectingLock(range, version, out completion))
        {
            return WaitForRangeCore(range, version, completion);
        }

        return ValueTask.CompletedTask;

        bool TryGetIntersectingLock(RingRange range, MembershipVersion version, [NotNullWhen(true)] out Task? completion)
        {
            foreach (var rangeLock in _rangeLocks)
            {
                if (rangeLock.Version <= version && range.Intersects(rangeLock.Range))
                {
                    completion = rangeLock.Completion.Task;
                    return true;
                }
            }

            completion = null;
            return false;
        }

        async ValueTask WaitForRangeCore(RingRange range, MembershipVersion version, Task? task)
        {
            if (task is not null)
            {
                await task;
            }

            if (_view.Version < version)
            {
                await RefreshViewAsync(version, _stoppedCts.Token);
            }

            while (TryGetIntersectingLock(range, version, out var completion))
            {
                await completion.WaitAsync(_stoppedCts.Token);
            }
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
        await this.RunOrQueueTask(async () =>
        {
            _drainSnapshotsCts.Cancel();
            if (_partitionSnapshots.Count > 0)
            {
                await _snapshotsDrainedTcs.Task.WaitAsync(token).SuppressThrowing();
            }
        });
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
        _stoppedCts.Cancel();
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
            while (!_stoppedCts.IsCancellationRequested)
            {
                try
                {
                    var previousRanges = _view.GetRanges(_id);
                    await foreach (var update in _clusterMembershipService.MembershipUpdates.WithCancellation(_stoppedCts.Token))
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
                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug("Updating view from '{PreviousVersion}' to '{Version}'. Now responsible for '{Range}' (Δ {DeltaPercent:0.00}%. {DeviationFromMean:0.00}% from ideal share).", previousUpdate.Version, update.Version, currentRanges, deltaSize, deviationFromMean);
                        }

                        ProcessMembershipUpdate(tasks, current);
                        tasks.RemoveAll(task => task.IsCompleted);

                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug("Updated view from '{PreviousVersion}' to '{Version}'.", previousUpdate.Version, update.Version);
                        }
                        _viewUpdates.Publish(current);
                        previousUpdate = update;
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
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Deleting '{Count}' entries located on now-defunct silo '{SiloAddress}'.", toRemove.Count, change.SiloAddress);
            }

            foreach (var grainAddress in toRemove)
            {
#if false
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Deleting '{GrainAddress}' located on now-defunct silo '{SiloAddress}'.", grainAddress, change.SiloAddress);
                }
#endif
                DeregisterCore(grainAddress);
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

        var removedRanges = previousRanges.Difference(currentRanges);
        var addedRanges = currentRanges.Difference(previousRanges);

        Debug.Assert(currentRanges.Size == previousRanges.Size + addedRanges.Size - removedRanges.Size);
        Debug.Assert(!removedRanges.Intersects(addedRanges));
        Debug.Assert(!removedRanges.Intersects(currentRanges));
        Debug.Assert(removedRanges.IsEmpty || removedRanges.Intersects(previousRanges));
        Debug.Assert(!addedRanges.Intersects(removedRanges));
        Debug.Assert(addedRanges.IsEmpty || addedRanges.Intersects(currentRanges));
        Debug.Assert(!addedRanges.Intersects(previousRanges));

        if (!removedRanges.IsEmpty)
        {
            tasks.Add(ReleaseRangesAsync(previous, current, removedRanges));
        }

        if (!addedRanges.IsEmpty)
        {
            tasks.Add(AcquireRangesAsync(previous, current, addedRanges));
        }
    }

    private async Task ReleaseRangesAsync(DirectoryMembershipSnapshot previous, DirectoryMembershipSnapshot current, RingRangeCollection removedRanges)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var range in removedRanges.Ranges)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Relinquishing ownership of range '{Range}'.", range);
            }

            _rangeLocks.Add((range, current.Version, tcs));
        }

        try
        {
            // Snapshot & remove everything not in the current range.
            // The new owner will have the opportunity to retrieve the snapshot as they take ownership.
            List<GrainAddress> removedAddresses = [];
            HashSet<SiloAddress> transferPartners = [];
            foreach (var removedRange in removedRanges)
            {
                // Wait for the range being removed to become valid.
                await WaitForRange(removedRange, previous.Version);

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
            }

            // Remove these addresses from the partition.
            foreach (var address in removedAddresses)
            {
                if (transferPartners.Count > 0)
                {
                    _logger.LogTrace("Evicting entry '{Address}' to snapshot.", address);
                }

                _directory.Remove(address.GrainId);
            }

            if (transferPartners.Count > 0)
            {
                _partitionSnapshots.Add(new PartitionSnapshotState(previous.Version, removedAddresses, transferPartners));
            }
            else
            {
                _logger.LogDebug("Dropping snapshot since there are no transfer partners.");
            }
        }
        finally
        {
            // Resume the suspended ranges.
            tcs.SetResult();
            foreach (var range in removedRanges.Ranges)
            {
                _rangeLocks.Remove((range, current.Version, tcs));
            }
        }
    }

    private async Task AcquireRangesAsync(DirectoryMembershipSnapshot previous, DirectoryMembershipSnapshot current, RingRangeCollection addedRanges)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var addedRange in addedRanges.Ranges)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Accepting ownership of range '{Range}'.", addedRange);
            }

            // Suspend this range and transfer state from the previous owner.
            // If the predecessor becomes unavailable or membership advances quickly, we will declare data loss and unlock the range.
            _rangeLocks.Add((addedRange, current.Version, tcs));
        }

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
                if (addedRanges.Intersects(previousOwnerRanges))
                {
                    tasks.Add(TransferSnapshotAsync(current, addedRanges, previousOwner, previous.Version));
                }
            }

            // Note: there should be no 'await' points before this point.
            // An await before this point would result in ranges not being locked synchronously.
            await Task.WhenAll(tasks).WaitAsync(_stoppedCts.Token).SuppressThrowing();
            if (_stoppedCts.IsCancellationRequested)
            {
                return;
            }

            success = tasks.All(t => t.Result);
        }
        else
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Non-contiguous view change detected: '{PreviousVersion}' to '{CurrentVersion}'. Performing recovery.",
                    previous.Version,
                    current.Version);
            }

            success = false;
        }

        var recovered = false;
        if (!success)
        {
            // Wait for previous versions to be unlocked before proceeding.
            foreach (var range in addedRanges)
            {
                await WaitForRange(range, previous.Version);
            }

            await RecoverPartitionRange(current, addedRanges);
            recovered = true;
        }

        // Resume the suspended ranges.
        tcs.SetResult();
        foreach (var addedRange in addedRanges.Ranges)
        {
            _rangeLocks.Remove((addedRange, current.Version, tcs));
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Completed transferring entries for range '{Range}' at version '{Version}' took {Elapsed}ms.{Recovered}", addedRanges, current.Version, stopwatch.ElapsedMilliseconds, recovered ? " Recovered" : "");
        }
    }

    private async Task<bool> TransferSnapshotAsync(DirectoryMembershipSnapshot current, RingRangeCollection addedRanges, SiloAddress previousOwner, MembershipVersion previousVersion)
    {
        try
        {
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Requesting entries for ranges '{Range}' from '{PreviousOwner}' at version '{PreviousVersion}'.", addedRanges, previousOwner, previousVersion);
            }

            var replica = GetReplica(previousOwner);

            // Alternatively, the previous owner could push the snapshot. The pull-based approach is used here because it is simpler.
            var snapshot = await replica.GetSnapshotAsync(current.Version, previousVersion, addedRanges).AsTask().WaitAsync(_stoppedCts.Token);

            if (snapshot is null)
            {
                _logger.LogWarning("Expected a valid snapshot from previous owner '{PreviousOwner}' for part of ranges '{Range}', but found none.", previousOwner, addedRanges);
                return false;
            }

            // The acknowledgement step lets the previous owner know that the snapshot has been received so that it can proceed.
            InvokeOnClusterMember(
                previousOwner,
                async () => await replica.AcknowledgeSnapshotTransferAsync(_id, previousVersion),
                false,
                nameof(IGrainDirectoryReplica.AcknowledgeSnapshotTransferAsync)).Ignore();

            // Wait for previous versions to be unlocked before proceeding.
            foreach (var range in addedRanges)
            {
                await WaitForRange(range, previousVersion);
            }

            // Incorporate the values into the grain directory.
            foreach (var entry in snapshot.GrainAddresses)
            {
                DebugAssertOwnership(current, entry.GrainId);

                _logger.LogTrace("Received '{Entry}' via snapshot from '{PreviousOwner}' for version '{Version}'.", entry, previousOwner, previousVersion);
                _directory[entry.GrainId] = entry;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Transferred '{Count}' entries for range '{Range}' from '{PreviousOwner}'.", snapshot.GrainAddresses.Count, addedRanges, previousOwner);
            }

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
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Recovering activations from ranges '{Range}' at version '{Version}'.", addedRanges, current.Version);
        }

        await foreach (var activations in GetRegisteredActivations(current, addedRanges, isValidation: false))
        {
            foreach (var entry in activations)
            {
                DebugAssertOwnership(current, entry.GrainId);
                _logger.LogTrace("Recovered '{Entry}' for version '{Version}'.", entry, current.Version);
                _directory[entry.GrainId] = entry;
            }
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Completed recovering activations from ranges '{Range}' at version '{Version}'.", addedRanges, current.Version);
        }
    }

    private async IAsyncEnumerable<List<GrainAddress>> GetRegisteredActivations(DirectoryMembershipSnapshot current, RingRangeCollection ranges, bool isValidation)
    {
        // Membership is guaranteed to be at least as recent as the current view.
        var clusterMembershipSnapshot = _clusterMembershipService.CurrentSnapshot;
        Debug.Assert(clusterMembershipSnapshot.Version >= current.Version);

        var tasks = new List<Task<List<GrainAddress>>>();
        foreach (var member in clusterMembershipSnapshot.Members.Values)
        {
            if (member.Status is not (SiloStatus.Active or SiloStatus.Joining or SiloStatus.ShuttingDown))
            {
                continue;
            }

            tasks.Add(GetRegisteredActivationsFromClusterMember(current.Version, ranges, member.SiloAddress, isValidation));
        }

        await Task.WhenAll(tasks).WaitAsync(_stoppedCts.Token).SuppressThrowing();
        if (_stoppedCts.IsCancellationRequested)
        {
            yield break;
        }

        foreach (var task in tasks)
        {
            yield return await task;
        }

        async Task<List<GrainAddress>> GetRegisteredActivationsFromClusterMember(MembershipVersion version, RingRangeCollection ranges, SiloAddress siloAddress, bool isValidation)
        {
            var stopwatch = ValueStopwatch.StartNew();
            var client = _grainFactory.GetSystemTarget<IGrainDirectoryReplicaClient>(Constants.DirectoryReplicaClientType, siloAddress);
            var result = await InvokeOnClusterMember(
                siloAddress,
                async () => await client.GetRegisteredActivations(version, ranges, isValidation),
                new Immutable<List<GrainAddress>>([]),
                nameof(GetRegisteredActivations));

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Recovered '{Count}' entries from silo '{SiloAddress}' for ranges '{Range}' at version '{Version}' in {ElapsedMilliseconds}ms.", result.Value.Count, siloAddress, ranges, version, stopwatch.Elapsed.TotalMilliseconds);
            }

            return result.Value;
        }
    }

    private async Task<T> InvokeOnClusterMember<T>(SiloAddress siloAddress, Func<Task<T>> func, T defaultValue, string operationName)
    {
        var clusterMembershipSnapshot = _clusterMembershipService.CurrentSnapshot;
        while (!_stoppedCts.IsCancellationRequested)
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

        _stoppedCts.Token.ThrowIfCancellationRequested();
        return defaultValue;
    }

    async ValueTask IGrainDirectoryReplicaTestHooks.CheckIntegrityAsync()
    {
        var current = _view;
        await WaitForRange(RingRange.Full, current.Version);
        _logger.LogInformation("Performing integrity check on directory at version '{Version}'.", current.Version);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fullRangeLock = (RingRange.Full, current.Version, tcs);
        _rangeLocks.Add(fullRangeLock);
        try
        {
            foreach (var entry in _directory)
            {
                DebugAssertOwnership(_view, entry.Key);
            }

            int missing = 0;
            int mismatched = 0;
            var total = 0;
            await foreach (var activationList in GetRegisteredActivations(current, current.GetRanges(_id), isValidation: true))
            {
                total += activationList.Count;
                foreach (var entry in activationList)
                {
                    if (!IsOwner(_view, entry.GrainId))
                    {
                        // The view has been refreshed since the request for registered activations was made.
                        if (_view.Version <= current.Version)
                        {
                            Debug.Fail("Invariant violated. This host was sent a registration which it should not have been.");
                        }

                        continue;
                    }

                    if (_directory.TryGetValue(entry.GrainId, out var existingEntry))
                    {
                        if (!existingEntry.Equals(entry))
                        {
                            ++mismatched;
                            _logger.LogError("Integrity violation: Recovered entry '{RecoveredRecord}' does not match existing entry '{LocalRecord}'.", entry, existingEntry);
                            Debug.Fail($"Integrity violation: Recovered entry '{entry}' does not match existing entry '{existingEntry}'.");
                        }
                    }
                    else
                    {
                        ++missing;
                        _logger.LogError("Integrity violation: Recovered entry '{RecoveredRecord}' not found in directory.", entry);
                        Debug.Fail($"Integrity violation: Recovered entry '{entry}' not found in directory.");
                    }
                }
            }

            _logger.LogInformation("Directory integrity check analyzed '{TotalRecordCount}' records, '{MissingRecordCount}' were missing, and '{MismatchedRecordCount}' mismatched.", total, missing, mismatched);
            return;
        }
        finally
        {
            tcs.SetResult();
            _rangeLocks.Remove(fullRangeLock);
        }
    }

    private sealed record class PartitionSnapshotState(
        MembershipVersion DirectoryMembershipVersion,
        List<GrainAddress> GrainAddresses,
        HashSet<SiloAddress> TransferPartners);
}
