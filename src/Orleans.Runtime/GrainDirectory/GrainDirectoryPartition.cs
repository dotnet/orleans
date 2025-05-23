using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Internal;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.Utilities;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

/// <summary>
/// Represents a single contiguous partition of the distributed grain directory.
/// </summary>
internal sealed partial class GrainDirectoryPartition : SystemTarget, IGrainDirectoryPartition, IGrainDirectoryTestHooks
{
    internal static SystemTargetGrainId CreateGrainId(SiloAddress siloAddress, int partitionIndex) => SystemTargetGrainId.Create(Constants.GrainDirectoryPartitionType, siloAddress, partitionIndex.ToString(CultureInfo.InvariantCulture));
    private readonly Dictionary<GrainId, GrainAddress> _directory = [];
    private readonly int _partitionIndex;
    private readonly DistributedGrainDirectory _owner;
    private readonly IInternalGrainFactory _grainFactory;
    private readonly CancellationTokenSource _drainSnapshotsCts = new();
    private readonly SiloAddress _id;
    private readonly ILogger<GrainDirectoryPartition> _logger;
    private readonly TaskCompletionSource _snapshotsDrainedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AsyncEnumerable<DirectoryMembershipSnapshot> _viewUpdates = new(
        DirectoryMembershipSnapshot.Default,
        (previous, proposed) => proposed.Version >= previous.Version,
        _ => { });

    // Ranges which cannot be served currently, eg because the partition is currently transferring them from a previous owner.
    // Requests in these ranges must wait for the range to become available.
    private readonly List<(RingRange Range, MembershipVersion Version, TaskCompletionSource Completion)> _rangeLocks = [];

    // Ranges which were previously at least partially owned by this partition, but which are pending transfer to a new partition.
    private readonly List<PartitionSnapshotState> _partitionSnapshots = [];

    // Tracked for diagnostic purposes only.
    private readonly List<Task> _viewChangeTasks = [];
    private CancellationToken ShutdownToken => _owner.OnStoppedToken;

    private RingRange _currentRange;

    /// <param name="partitionIndex">The index of this partition on this silo. Each silo hosts a fixed number of dynamically sized partitions.</param>
    public GrainDirectoryPartition(
        int partitionIndex,
        DistributedGrainDirectory owner,
        IInternalGrainFactory grainFactory,
        SystemTargetShared shared) : base(CreateGrainId(shared.SiloAddress, partitionIndex), shared)
    {
        _partitionIndex = partitionIndex;
        _owner = owner;
        _grainFactory = grainFactory;
        _id = shared.SiloAddress;
        _logger = shared.LoggerFactory.CreateLogger<GrainDirectoryPartition>();
        shared.ActivationDirectory.RecordNewTarget(this);
    }

    // The current directory membership snapshot.
    public DirectoryMembershipSnapshot CurrentView { get; private set; } = DirectoryMembershipSnapshot.Default;

    public async ValueTask<DirectoryMembershipSnapshot> RefreshViewAsync(MembershipVersion version, CancellationToken cancellationToken)
    {
        _ = _owner.RefreshViewAsync(version, cancellationToken);
        if (CurrentView.Version <= version)
        {
            await foreach (var view in _viewUpdates.WithCancellation(cancellationToken))
            {
                if (view.Version >= version)
                {
                    break;
                }
            }
        }

        return CurrentView;
    }

    async ValueTask<GrainDirectoryPartitionSnapshot?> IGrainDirectoryPartition.GetSnapshotAsync(MembershipVersion version, MembershipVersion rangeVersion, RingRange range)
    {
        LogTraceGetSnapshotAsync(_logger, version, rangeVersion, range);

        // Wait for the range to be unlocked.
        await WaitForRange(range, version);

        ShutdownToken.ThrowIfCancellationRequested();
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
                if (range.Contains(address.GrainId))
                {
                    partitionAddresses.Add(address);
                }
            }

            var rangeSnapshot = new GrainDirectoryPartitionSnapshot(rangeVersion, partitionAddresses);
            LogDebugTransferringEntries(_logger, partitionAddresses.Count, range, rangeVersion);

            return rangeSnapshot;
        }

        LogWarningRequestForSnapshot(_logger, version, rangeVersion, range);
        return null;
    }

    ValueTask<bool> IGrainDirectoryPartition.AcknowledgeSnapshotTransferAsync(SiloAddress silo, int partitionIndex, MembershipVersion rangeVersion)
    {
        RemoveSnapshotTransferPartner(
            (silo, partitionIndex, rangeVersion),
            snapshotFilter: (state, snapshot) => snapshot.DirectoryMembershipVersion == state.rangeVersion,
            partnerFilter: (state, silo, partitionIndex) => silo.Equals(state.silo) && partitionIndex == state.partitionIndex);
        return new(true);
    }

    private void RemoveSnapshotTransferPartner<TState>(TState state, Func<TState, PartitionSnapshotState, bool> snapshotFilter, Func<TState, SiloAddress, int, bool> partnerFilter)
    {
        for (var i = 0; i < _partitionSnapshots.Count; ++i)
        {
            var partitionSnapshot = _partitionSnapshots[i];
            if (!snapshotFilter(state, partitionSnapshot))
            {
                continue;
            }

            var partners = partitionSnapshot.TransferPartners;
            partners.RemoveWhere(p => partnerFilter(state, p.SiloAddress, p.PartitionIndex));
            if (partners.Count == 0)
            {
                _partitionSnapshots.RemoveAt(i);
                --i;

                LogDebugRemovingSnapshot(_logger, partitionSnapshot.DirectoryMembershipVersion, string.Join(", ", _partitionSnapshots.Select(s => s.DirectoryMembershipVersion)));

                // If shutdown has been requested and there are no more pending snapshots, signal completion.
                if (_drainSnapshotsCts.IsCancellationRequested && _partitionSnapshots.Count == 0)
                {
                    _snapshotsDrainedTcs.TrySetResult();
                }
            }
        }
    }

    [Conditional("DEBUG")]
    private void DebugAssertOwnership(GrainId grainId) => DebugAssertOwnership(CurrentView, grainId);

    [Conditional("DEBUG")]
    private void DebugAssertOwnership(DirectoryMembershipSnapshot view, GrainId grainId)
    {
        if (!view.TryGetOwner(grainId, out var owner, out var partitionReference))
        {
            Debug.Fail($"Could not find owner for grain grain '{grainId}' in view '{view}'.");
        }

        if (!_id.Equals(owner))
        {
            Debug.Fail($"'{_id}' expected to be the owner of grain '{grainId}', but the owner is '{owner}'.");
        }

        if (!GrainId.Equals(partitionReference.GetGrainId()))
        {
            Debug.Fail($"'{GrainId}' expected to be the owner of grain '{grainId}', but the owner is '{partitionReference.GetGrainId()}'.");
        }
    }

    private bool IsOwner(DirectoryMembershipSnapshot view, GrainId grainId) => view.TryGetOwner(grainId, out _, out var partitionReference) && GrainId.Equals(partitionReference.GetGrainId());

    private ValueTask WaitForRange(GrainId grainId, MembershipVersion version) => WaitForRange(RingRange.FromPoint(grainId.GetUniformHashCode()), version);

    private ValueTask WaitForRange(RingRange range, MembershipVersion version)
    {
        GrainRuntime.CheckRuntimeContext(this);
        Task? completion = null;
        if (CurrentView.Version < version || TryGetIntersectingLock(range, version, out completion))
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

            if (CurrentView.Version < version)
            {
                await RefreshViewAsync(version, ShutdownToken);
            }

            while (TryGetIntersectingLock(range, version, out var completion))
            {
                await completion.WaitAsync(ShutdownToken);
            }
        }
    }

    public IGrainDirectoryPartition GetPartitionReference(SiloAddress address, int partitionIndex) => _grainFactory.GetSystemTarget<IGrainDirectoryPartition>(CreateGrainId(address, partitionIndex).GrainId);

    internal async Task OnShuttingDown(CancellationToken token)
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
    internal Task OnSiloRemovedFromClusterAsync(ClusterMember change) =>
        this.QueueAction(
            static state => state.Self.OnSiloRemovedFromCluster(state.Change),
            (Self: this, Change: change),
            nameof(OnSiloRemovedFromCluster));

    private void OnSiloRemovedFromCluster(ClusterMember change)
    {
        GrainRuntime.CheckRuntimeContext(this);
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
            LogDebugDeletingEntries(_logger, toRemove.Count, change.SiloAddress);

            foreach (var grainAddress in toRemove)
            {
                DeregisterCore(grainAddress);
            }
        }

        RemoveSnapshotTransferPartner(
            change.SiloAddress,
            snapshotFilter: (state, snapshot) => true,
            partnerFilter: (state, silo, partitionIndex) => silo.Equals(state));
    }

    internal Task OnRecoveringPartition(MembershipVersion version, RingRange range, SiloAddress siloAddress, int partitionIndex) =>
        this.QueueTask(
            async () =>
            {
                try
                {
                    await WaitForRange(range, version);
                }
                catch (Exception exception)
                {
                    LogWarningErrorWaitingForRangeToUnlock(_logger, exception);
                }

                // Remove all snapshots that are associated with the given partition prior or equal to the specified version.
                RemoveSnapshotTransferPartner(
                    (Version: version, SiloAddress: siloAddress, PartitionIndex: partitionIndex),
                    snapshotFilter: (state, snapshot) => snapshot.DirectoryMembershipVersion <= state.Version,
                    partnerFilter: (state, silo, partitionIndex) => partitionIndex == state.PartitionIndex && silo.Equals(state.SiloAddress));
            });

    internal Task ProcessMembershipUpdateAsync(DirectoryMembershipSnapshot current) =>
        this.QueueAction(
            static state => state.Self.ProcessMembershipUpdate(state.Current),
            (Self: this, Current: current),
            nameof(ProcessMembershipUpdate));

    private void ProcessMembershipUpdate(DirectoryMembershipSnapshot current)
    {
        GrainRuntime.CheckRuntimeContext(this);

        _viewChangeTasks.RemoveAll(task => task.IsCompleted);

        LogTraceObservedMembershipVersion(_logger, current.Version);

        var previous = CurrentView;
        CurrentView = current;

        var previousRange = previous.GetRange(_id, _partitionIndex);
        _currentRange = current.GetRange(_id, _partitionIndex);

        var removedRange = previousRange.Difference(_currentRange).SingleOrDefault();
        var addedRange = _currentRange.Difference(previousRange).SingleOrDefault();

#if DEBUG
        Debug.Assert(addedRange.IsEmpty ^ removedRange.IsEmpty || addedRange.IsEmpty && removedRange.IsEmpty); // Either the range grew or it shrank, but not both.
        Debug.Assert(previousRange.Difference(_currentRange).Count() < 2);
        Debug.Assert(_currentRange.Difference(previousRange).Count() < 2);
        Debug.Assert(_currentRange.Size == previousRange.Size + addedRange.Size - removedRange.Size);
        Debug.Assert(!removedRange.Intersects(addedRange));
        Debug.Assert(!removedRange.Intersects(_currentRange));
        Debug.Assert(removedRange.IsEmpty || removedRange.Intersects(previousRange));
        Debug.Assert(!addedRange.Intersects(removedRange));
        Debug.Assert(addedRange.IsEmpty || addedRange.Intersects(_currentRange));
        Debug.Assert(!addedRange.Intersects(previousRange));
        Debug.Assert(previousRange.IsEmpty || _currentRange.IsEmpty || previousRange.Start == _currentRange.Start);
#endif

        if (!removedRange.IsEmpty)
        {
            _viewChangeTasks.Add(ReleaseRangeAsync(previous, current, removedRange));
        }

        if (!addedRange.IsEmpty)
        {
            _viewChangeTasks.Add(AcquireRangeAsync(previous, current, addedRange));
        }

        _viewUpdates.Publish(current);
    }

    private async Task ReleaseRangeAsync(DirectoryMembershipSnapshot previous, DirectoryMembershipSnapshot current, RingRange removedRange)
    {
        GrainRuntime.CheckRuntimeContext(this);
        var (tcs, sw) = LockRange(removedRange, current.Version);
        LogDebugRelinquishingOwnership(_logger, removedRange, current.Version);

        try
        {
            // Snapshot & remove everything not in the current range.
            // The new owner will have the opportunity to retrieve the snapshot as they take ownership.
            List<GrainAddress> removedAddresses = [];
            HashSet<(SiloAddress, int)> transferPartners = [];

            // Wait for the range being removed to become valid.
            await WaitForRange(removedRange, previous.Version);

            GrainRuntime.CheckRuntimeContext(this);

            foreach (var (range, ownerIndex, partitionIndex) in current.RangeOwners)
            {
                if (range.Intersects(removedRange))
                {
                    var owner = current.Members[ownerIndex];
                    Debug.Assert(!_id.Equals(owner));
                    transferPartners.Add((owner, partitionIndex));
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
                if (transferPartners.Count > 0)
                {
                    LogTraceEvictingEntry(_logger, address);
                }

                _directory.Remove(address.GrainId);
            }

            var isContiguous = current.Version.Value == previous.Version.Value + 1;
            if (!isContiguous)
            {
                LogDebugEncounteredNonContiguousUpdate(_logger, previous.Version, current.Version, removedRange);
                return;
            }

            if (transferPartners.Count == 0)
            {
                LogDebugNoTransferPartners(_logger, removedRange, current.Version);
                return;
            }

            _partitionSnapshots.Add(new PartitionSnapshotState(previous.Version, removedAddresses, transferPartners));
        }
        finally
        {
            UnlockRange(removedRange, current.Version, tcs, sw.Elapsed, "release");
        }
    }

    private async Task AcquireRangeAsync(DirectoryMembershipSnapshot previous, DirectoryMembershipSnapshot current, RingRange addedRange)
    {
        GrainRuntime.CheckRuntimeContext(this);
        // Suspend the range and transfer state from the previous owners.
        // If the predecessor becomes unavailable or membership advances quickly, we will declare data loss and unlock the range.
        var (tcs, sw) = LockRange(addedRange, current.Version);

        try
        {
            CoarseStopwatch stopwatch = default;
            LogDebugAcquiringRange(_logger, addedRange);
            stopwatch = CoarseStopwatch.StartNew();

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
                    var previousOwnerRanges = previous.GetMemberRangesByPartition(previousOwner);
                    for (var partitionIndex = 0; partitionIndex < previousOwnerRanges.Length; partitionIndex++)
                    {
                        var previousOwnerRange = previousOwnerRanges[partitionIndex];
                        if (previousOwnerRange.Intersects(addedRange))
                        {
                            tasks.Add(TransferSnapshotAsync(current, addedRange, previousOwner, partitionIndex, previous.Version));
                        }
                    }
                }

                // Note: there should be no 'await' points before this point.
                // An await before this point would result in ranges not being locked synchronously.
                await Task.WhenAll(tasks).WaitAsync(ShutdownToken).SuppressThrowing();
                if (ShutdownToken.IsCancellationRequested)
                {
                    return;
                }

                success = tasks.All(t => t.Result);
            }
            else
            {
                LogDebugNonContiguousViewChange(_logger, previous.Version, current.Version);
                success = false;
            }

            var recovered = false;
            if (!success)
            {
                // Wait for previous versions to be unlocked before proceeding.
                await WaitForRange(addedRange, previous.Version);

                await RecoverPartitionRange(current, addedRange);
                recovered = true;
            }

            LogDebugCompletedTransferringEntries(_logger, addedRange, current.Version, stopwatch.ElapsedMilliseconds, recovered);
        }
        finally
        {
            UnlockRange(addedRange, current.Version, tcs, sw.Elapsed, "acquire");
        }
    }

    private (TaskCompletionSource Lock, ValueStopwatch Stopwatch) LockRange(RingRange range, MembershipVersion version)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rangeLocks.Add((range, version, tcs));
        return (tcs, ValueStopwatch.StartNew());
    }

    private void UnlockRange(RingRange range, MembershipVersion version, TaskCompletionSource tcs, TimeSpan heldDuration, string operationName)
    {
        DirectoryInstruments.RangeLockHeldDuration.Record((long)heldDuration.TotalMilliseconds);
        if (ShutdownToken.IsCancellationRequested)
        {
            // If the partition is stopped, the range is never unlocked and the task is cancelled instead.
            tcs.SetCanceled(ShutdownToken);
        }
        else
        {
            tcs.SetResult();
            _rangeLocks.Remove((range, version, tcs));
        }
    }

    private async Task<bool> TransferSnapshotAsync(DirectoryMembershipSnapshot current, RingRange addedRange, SiloAddress previousOwner, int partitionIndex, MembershipVersion previousVersion)
    {
        try
        {
            var stopwatch = ValueStopwatch.StartNew();
            LogTraceRequestingEntries(_logger, addedRange, previousOwner, previousVersion);

            var partition = GetPartitionReference(previousOwner, partitionIndex);

            // Alternatively, the previous owner could push the snapshot. The pull-based approach is used here because it is simpler.
            var snapshot = await partition.GetSnapshotAsync(current.Version, previousVersion, addedRange).AsTask().WaitAsync(ShutdownToken);

            if (snapshot is null)
            {
                LogWarningExpectedValidSnapshot(_logger, previousOwner, addedRange);
                return false;
            }

            // The acknowledgement step lets the previous owner know that the snapshot has been received so that it can proceed.
            InvokeOnClusterMember(
                previousOwner,
                async () => await partition.AcknowledgeSnapshotTransferAsync(_id, _partitionIndex, previousVersion),
                false,
                nameof(IGrainDirectoryPartition.AcknowledgeSnapshotTransferAsync)).Ignore();

            // Wait for previous versions to be unlocked before proceeding.
            await WaitForRange(addedRange, previousVersion);

            // Incorporate the values into the grain directory.
            foreach (var entry in snapshot.GrainAddresses)
            {
                DebugAssertOwnership(current, entry.GrainId);

                LogTraceReceivedEntry(_logger, entry, previousOwner, previousVersion);
                _directory[entry.GrainId] = entry;
            }

            LogDebugTransferredEntries(_logger, snapshot.GrainAddresses.Count, addedRange, previousOwner);

            DirectoryInstruments.SnapshotTransferCount.Add(1);
            DirectoryInstruments.SnapshotTransferDuration.Record((long)stopwatch.Elapsed.TotalMilliseconds);

            return true;
        }
        catch (Exception exception)
        {
            if (exception is SiloUnavailableException)
            {
                LogWarningRemoteHostUnavailable(_logger, addedRange);
            }
            else
            {
                LogWarningErrorTransferringOwnership(_logger, exception, addedRange);
            }

            return false;
        }
    }

    private async Task RecoverPartitionRange(DirectoryMembershipSnapshot current, RingRange addedRange)
    {
        var stopwatch = ValueStopwatch.StartNew();
        GrainRuntime.CheckRuntimeContext(this);
        LogDebugRecoveringActivations(_logger, addedRange, current.Version);

        await foreach (var activations in GetRegisteredActivations(current, addedRange, isValidation: false))
        {
            GrainRuntime.CheckRuntimeContext(this);
            foreach (var entry in activations)
            {
                DebugAssertOwnership(current, entry.GrainId);
                LogTraceRecoveredEntry(_logger, entry, current.Version);
                _directory[entry.GrainId] = entry;
            }
        }

        DirectoryInstruments.RangeRecoveryCount.Add(1);
        DirectoryInstruments.RangeRecoveryDuration.Record((long)stopwatch.Elapsed.TotalMilliseconds);
        LogDebugCompletedRecoveringActivations(_logger, addedRange, current.Version, stopwatch.Elapsed);
    }

    private async IAsyncEnumerable<List<GrainAddress>> GetRegisteredActivations(DirectoryMembershipSnapshot current, RingRange range, bool isValidation)
    {
        // Membership is guaranteed to be at least as recent as the current view.
        var clusterMembershipSnapshot = _owner.ClusterMembershipSnapshot;
        Debug.Assert(clusterMembershipSnapshot.Version >= current.Version);

        var tasks = new List<Task<List<GrainAddress>>>();
        foreach (var member in clusterMembershipSnapshot.Members.Values)
        {
            if (member.Status is not (SiloStatus.Active or SiloStatus.Joining or SiloStatus.ShuttingDown))
            {
                continue;
            }

            tasks.Add(GetRegisteredActivationsFromClusterMember(current.Version, range, member.SiloAddress, isValidation));
        }

        await Task.WhenAll(tasks).WaitAsync(ShutdownToken).SuppressThrowing();
        if (ShutdownToken.IsCancellationRequested)
        {
            yield break;
        }

        foreach (var task in tasks)
        {
            yield return await task;
        }

        async Task<List<GrainAddress>> GetRegisteredActivationsFromClusterMember(MembershipVersion version, RingRange range, SiloAddress siloAddress, bool isValidation)
        {
            var stopwatch = ValueStopwatch.StartNew();
            var client = _grainFactory.GetSystemTarget<IGrainDirectoryClient>(Constants.GrainDirectoryType, siloAddress);
            var result = await InvokeOnClusterMember(
                siloAddress,
                async () =>
                {
                    var innerSw = ValueStopwatch.StartNew();
                    Immutable<List<GrainAddress>> result = default;
                        if (isValidation)
                        {
                            result = await client.GetRegisteredActivations(version, range, isValidation: true);
                        }
                        else
                        {
                            result = await client.RecoverRegisteredActivations(version, range, _id, _partitionIndex);
                        }

                    return result;
                },
                new Immutable<List<GrainAddress>>([]),
                nameof(GetRegisteredActivations));

            LogDebugRecoveredEntries(_logger, result.Value.Count, siloAddress, range, version, stopwatch.Elapsed.TotalMilliseconds);

            return result.Value;
        }
    }

    private async Task<T> InvokeOnClusterMember<T>(SiloAddress siloAddress, Func<Task<T>> func, T defaultValue, string operationName)
    {
        GrainRuntime.CheckRuntimeContext(this);
        var clusterMembershipSnapshot = _owner.ClusterMembershipSnapshot;
        while (!ShutdownToken.IsCancellationRequested)
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
                if (ex is not OrleansMessageRejectionException)
                {
                    LogErrorErrorInvokingOperation(_logger, ex, operationName, siloAddress);
                }

                await _owner.RefreshViewAsync(default, CancellationToken.None);
                if (_owner.ClusterMembershipSnapshot.Version == clusterMembershipSnapshot.Version)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100));
                }

                clusterMembershipSnapshot = _owner.ClusterMembershipSnapshot;
            }
        }

        ShutdownToken.ThrowIfCancellationRequested();
        return defaultValue;
    }

    async ValueTask IGrainDirectoryTestHooks.CheckIntegrityAsync()
    {
        GrainRuntime.CheckRuntimeContext(this);
        var current = CurrentView;
        var range = _currentRange;
        Debug.Assert(range.Equals(current.GetRange(_id, _partitionIndex)));

        await WaitForRange(RingRange.Full, current.Version);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rangeLocks.Add((RingRange.Full, current.Version, tcs));
        try
        {
            foreach (var entry in _directory)
            {
                if (!range.Contains(entry.Key))
                {
                    Debug.Fail($"Invariant violated. This host is not the owner of grain '{entry.Key}'.");
                }

                DebugAssertOwnership(current, entry.Key);
            }

            var missing = 0;
            var mismatched = 0;
            var total = 0;
            await foreach (var activationList in GetRegisteredActivations(current, range, isValidation: true))
            {
                total += activationList.Count;
                foreach (var entry in activationList)
                {
                    if (!IsOwner(current, entry.GrainId))
                    {
                        // The view has been refreshed since the request for registered activations was made.
                        if (current.Version <= current.Version)
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
                            LogErrorIntegrityViolation(_logger, entry, existingEntry);
                            Debug.Fail($"Integrity violation: Recovered entry '{entry}' does not match existing entry '{existingEntry}'.");
                        }
                    }
                    else
                    {
                        ++missing;
                        LogErrorIntegrityViolation(_logger, entry);
                        Debug.Fail($"Integrity violation: Recovered entry '{entry}' not found in directory.");
                    }
                }
            }
        }
        finally
        {
            if (ShutdownToken.IsCancellationRequested)
            {
                tcs.SetCanceled(ShutdownToken);
            }
            else
            {
                tcs.SetResult();
            }

            _rangeLocks.Remove((RingRange.Full, current.Version, tcs));
        }
    }

    private sealed record class PartitionSnapshotState(
        MembershipVersion DirectoryMembershipVersion,
        List<GrainAddress> GrainAddresses,
        HashSet<(SiloAddress SiloAddress, int PartitionIndex)> TransferPartners);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "GetSnapshotAsync('{Version}', '{RangeVersion}', '{Range}')"
    )]
    private static partial void LogTraceGetSnapshotAsync(ILogger logger, MembershipVersion version, MembershipVersion rangeVersion, RingRange range);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Transferring '{Count}' entries in range '{Range}' from version '{Version}' snapshot."
    )]
    private static partial void LogDebugTransferringEntries(ILogger logger, int count, RingRange range, MembershipVersion version);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Received a request for a snapshot which this partition does not have, version '{Version}', range version '{RangeVersion}', range '{Range}'."
    )]
    private static partial void LogWarningRequestForSnapshot(ILogger logger, MembershipVersion version, MembershipVersion rangeVersion, RingRange range);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Removing version '{Version}' snapshot. Current snapshots: [{CurrentSnapshots}]."
    )]
    private static partial void LogDebugRemovingSnapshot(ILogger logger, MembershipVersion version, string currentSnapshots);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Deleting '{Count}' entries located on now-defunct silo '{SiloAddress}'."
    )]
    private static partial void LogDebugDeletingEntries(ILogger logger, int count, SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Error waiting for range to unlock."
    )]
    private static partial void LogWarningErrorWaitingForRangeToUnlock(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Observed membership version '{Version}'."
    )]
    private static partial void LogTraceObservedMembershipVersion(ILogger logger, MembershipVersion version);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Relinquishing ownership of range '{Range}' at version '{Version}'."
    )]
    private static partial void LogDebugRelinquishingOwnership(ILogger logger, RingRange range, MembershipVersion version);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Evicting entry '{Address}' to snapshot."
    )]
    private static partial void LogTraceEvictingEntry(ILogger logger, GrainAddress address);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Encountered non-contiguous update from '{Previous}' to '{Current}' while releasing range '{Range}'. Dropping snapshot."
    )]
    private static partial void LogDebugEncounteredNonContiguousUpdate(ILogger logger, MembershipVersion previous, MembershipVersion current, RingRange range);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "No transfer partners for snapshot of range '{Range}' at version '{Version}'. Dropping snapshot."
    )]
    private static partial void LogDebugNoTransferPartners(ILogger logger, RingRange range, MembershipVersion version);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Acquiring range '{Range}'."
    )]
    private static partial void LogDebugAcquiringRange(ILogger logger, RingRange range);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Non-contiguous view change detected: '{PreviousVersion}' to '{CurrentVersion}'. Performing recovery."
    )]
    private static partial void LogDebugNonContiguousViewChange(ILogger logger, MembershipVersion previousVersion, MembershipVersion currentVersion);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Completed transferring entries for range '{Range}' at version '{Version}' took {Elapsed}ms.{Recovered}"
    )]
    private static partial void LogDebugCompletedTransferringEntries(ILogger logger, RingRange range, MembershipVersion version, long elapsed, bool recovered);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Requesting entries for ranges '{Range}' from '{PreviousOwner}' at version '{PreviousVersion}'."
    )]
    private static partial void LogTraceRequestingEntries(ILogger logger, RingRange range, SiloAddress previousOwner, MembershipVersion previousVersion);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Expected a valid snapshot from previous owner '{PreviousOwner}' for part of ranges '{Range}', but found none."
    )]
    private static partial void LogWarningExpectedValidSnapshot(ILogger logger, SiloAddress previousOwner, RingRange range);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Received '{Entry}' via snapshot from '{PreviousOwner}' for version '{Version}'."
    )]
    private static partial void LogTraceReceivedEntry(ILogger logger, GrainAddress entry, SiloAddress previousOwner, MembershipVersion version);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Transferred '{Count}' entries for range '{Range}' from '{PreviousOwner}'."
    )]
    private static partial void LogDebugTransferredEntries(ILogger logger, int count, RingRange range, SiloAddress previousOwner);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Remote host became unavailable while transferring ownership of range '{Range}'. Recovery will be performed."
    )]
    private static partial void LogWarningRemoteHostUnavailable(ILogger logger, RingRange range);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Error transferring ownership of range '{Range}'. Recovery will be performed."
    )]
    private static partial void LogWarningErrorTransferringOwnership(ILogger logger, Exception exception, RingRange range);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Recovering activations from range '{Range}' at version '{Version}'."
    )]
    private static partial void LogDebugRecoveringActivations(ILogger logger, RingRange range, MembershipVersion version);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Recovered '{Entry}' for version '{Version}'."
    )]
    private static partial void LogTraceRecoveredEntry(ILogger logger, GrainAddress entry, MembershipVersion version);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Completed recovering activations from range '{Range}' at version '{Version}' took '{Elapsed}'."
    )]
    private static partial void LogDebugCompletedRecoveringActivations(ILogger logger, RingRange range, MembershipVersion version, TimeSpan elapsed);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Recovered '{Count}' entries from silo '{SiloAddress}' for ranges '{Range}' at version '{Version}' in {ElapsedMilliseconds}ms."
    )]
    private static partial void LogDebugRecoveredEntries(ILogger logger, int count, SiloAddress siloAddress, RingRange range, MembershipVersion version, double elapsedMilliseconds);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error invoking operation '{Operation}' on silo '{SiloAddress}'."
    )]
    private static partial void LogErrorErrorInvokingOperation(ILogger logger, Exception exception, string operation, SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Integrity violation: Recovered entry '{RecoveredRecord}' does not match existing entry '{LocalRecord}'."
    )]
    private static partial void LogErrorIntegrityViolation(ILogger logger, GrainAddress recoveredRecord, GrainAddress localRecord);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Integrity violation: Recovered entry '{RecoveredRecord}' not found in directory."
    )]
    private static partial void LogErrorIntegrityViolation(ILogger logger, GrainAddress recoveredRecord);
}
