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
/// <param name="partitionIndex">The index of this partition on this silo. Each silo hosts a fixed number of dynamically sized partitions.</param>
internal sealed partial class GrainDirectoryPartition(
    int partitionIndex,
    DistributedGrainDirectory owner,
    ILocalSiloDetails localSiloDetails,
    ILoggerFactory loggerFactory,
    IServiceProvider serviceProvider,
    IInternalGrainFactory grainFactory)
    : SystemTarget(CreateGrainId(localSiloDetails.SiloAddress, partitionIndex), localSiloDetails.SiloAddress, loggerFactory), IGrainDirectoryPartition, IGrainDirectoryTestHooks
{
    internal static SystemTargetGrainId CreateGrainId(SiloAddress siloAddress, int partitionIndex) => SystemTargetGrainId.Create(Constants.GrainDirectoryPartition, siloAddress, partitionIndex.ToString(CultureInfo.InvariantCulture));
    private readonly Dictionary<GrainId, GrainAddress> _directory = [];
    private readonly int _partitionIndex = partitionIndex;
    private readonly DistributedGrainDirectory _owner = owner;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IInternalGrainFactory _grainFactory = grainFactory;
    private readonly CancellationTokenSource _drainSnapshotsCts = new();
    private readonly SiloAddress _id = localSiloDetails.SiloAddress;
    private readonly ILogger<GrainDirectoryPartition> _logger = loggerFactory.CreateLogger<GrainDirectoryPartition>();
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
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            Log.GetSnapshotAsync(_logger, version, rangeVersion, range);
        }

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
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                Log.TransferringEntries(_logger, partitionAddresses.Count, range, rangeVersion);
            }

            return rangeSnapshot;
        }

        Log.ReceivedRequestForSnapshot(_logger, version, rangeVersion, range);
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

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    Log.RemovingSnapshot(_logger, partitionSnapshot.DirectoryMembershipVersion, string.Join(", ", _partitionSnapshots.Select(s => s.DirectoryMembershipVersion)));
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
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                Log.DeletingEntries(_logger, toRemove.Count, change.SiloAddress);
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
                    Log.ErrorWaitingForRange(_logger, exception);
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

        if (_logger.IsEnabled(LogLevel.Trace))
        {
            Log.ObservedMembershipVersion(_logger, current.Version);
        }

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
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            Log.RelinquishingOwnership(_logger, removedRange, current.Version);
        }

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
                    _logger.LogTrace("Evicting entry '{Address}' to snapshot.", address);
                }

                _directory.Remove(address.GrainId);
            }

            var isContiguous = current.Version.Value == previous.Version.Value + 1;
            if (!isContiguous)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    Log.EncounteredNonContiguousUpdate(_logger, previous.Version, current.Version, removedRange);
                }

                return;
            }

            if (transferPartners.Count == 0)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    Log.NoTransferPartners(_logger, removedRange, current.Version);
                }

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
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                Log.AcquiringRange(_logger, addedRange);
                stopwatch = CoarseStopwatch.StartNew();
            }

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
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    Log.NonContiguousViewChange(_logger, previous.Version, current.Version, addedRange);
                }

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

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                Log.CompletedTransferringEntries(_logger, addedRange, current.Version, stopwatch.ElapsedMilliseconds, recovered);
            }
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
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                Log.RequestingEntries(_logger, addedRange, previousOwner, previousVersion);
            }

            var partition = GetPartitionReference(previousOwner, partitionIndex);

            // Alternatively, the previous owner could push the snapshot. The pull-based approach is used here because it is simpler.
            var snapshot = await partition.GetSnapshotAsync(current.Version, previousVersion, addedRange).AsTask().WaitAsync(ShutdownToken);

            if (snapshot is null)
            {
                Log.ExpectedValidSnapshot(_logger, previousOwner, addedRange);
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

                Log.ReceivedEntryViaSnapshot(_logger, entry, previousOwner, previousVersion);
                _directory[entry.GrainId] = entry;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                Log.TransferredEntries(_logger, snapshot.GrainAddresses.Count, addedRange, previousOwner);
            }

            DirectoryInstruments.SnapshotTransferCount.Add(1);
            DirectoryInstruments.SnapshotTransferDuration.Record((long)stopwatch.Elapsed.TotalMilliseconds);

            return true;
        }
        catch (Exception exception)
        {
            if (exception is SiloUnavailableException)
            {
                Log.RemoteHostUnavailable(_logger, addedRange);
            }
            else
            {
                Log.ErrorTransferringOwnership(_logger, exception, addedRange);
            }

            return false;
        }
    }

    private async Task RecoverPartitionRange(DirectoryMembershipSnapshot current, RingRange addedRange)
    {
        var stopwatch = ValueStopwatch.StartNew();
        GrainRuntime.CheckRuntimeContext(this);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            Log.RecoveringActivations(_logger, addedRange, current.Version);
        }

        await foreach (var activations in GetRegisteredActivations(current, addedRange, isValidation: false))
        {
            GrainRuntime.CheckRuntimeContext(this);
            foreach (var entry in activations)
            {
                DebugAssertOwnership(current, entry.GrainId);
                Log.RecoveredEntry(_logger, entry, current.Version);
                _directory[entry.GrainId] = entry;
            }
        }

        DirectoryInstruments.RangeRecoveryCount.Add(1);
        DirectoryInstruments.RangeRecoveryDuration.Record((long)stopwatch.Elapsed.TotalMilliseconds);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            Log.CompletedRecoveringActivations(_logger, addedRange, current.Version, stopwatch.Elapsed);
        }
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
            var client = _grainFactory.GetSystemTarget<IGrainDirectoryClient>(Constants.GrainDirectory, siloAddress);
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

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                Log.RecoveredEntries(_logger, result.Value.Count, siloAddress, range, version, stopwatch.Elapsed.TotalMilliseconds);
            }

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
                    Log.ErrorInvokingOperation(_logger, ex, operationName, siloAddress);
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
                            Log.IntegrityViolationRecoveredEntry(_logger, entry, existingEntry);
                            Debug.Fail($"Integrity violation: Recovered entry '{entry}' does not match existing entry '{existingEntry}'.");
                        }
                    }
                    else
                    {
                        ++missing;
                        Log.IntegrityViolationRecoveredEntryNotFound(_logger, entry);
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

    private static partial class Log
    {
        [LoggerMessage(1, LogLevel.Trace, "GetSnapshotAsync('{Version}', '{RangeVersion}', '{Range}')")]
        public static partial void GetSnapshotAsync(ILogger logger, MembershipVersion version, MembershipVersion rangeVersion, RingRange range);

        [LoggerMessage(2, LogLevel.Debug, "Transferring '{Count}' entries in range '{Range}' from version '{Version}' snapshot.")]
        public static partial void TransferringEntries(ILogger logger, int count, RingRange range, MembershipVersion version);

        [LoggerMessage(3, LogLevel.Warning, "Received a request for a snapshot which this partition does not have, version '{Version}', range version '{RangeVersion}', range '{Range}'.")]
        public static partial void ReceivedRequestForSnapshot(ILogger logger, MembershipVersion version, MembershipVersion rangeVersion, RingRange range);

        [LoggerMessage(4, LogLevel.Debug, "Removing version '{Version}' snapshot. Current snapshots: [{CurrentSnapshots}].")]
        public static partial void RemovingSnapshot(ILogger logger, MembershipVersion version, string currentSnapshots);

        [LoggerMessage(5, LogLevel.Debug, "Deleting '{Count}' entries located on now-defunct silo '{SiloAddress}'.")]
        public static partial void DeletingEntries(ILogger logger, int count, SiloAddress siloAddress);

        [LoggerMessage(6, LogLevel.Warning, "Error waiting for range to unlock.")]
        public static partial void ErrorWaitingForRange(ILogger logger, Exception exception);

        [LoggerMessage(7, LogLevel.Trace, "Observed membership version '{Version}'.")]
        public static partial void ObservedMembershipVersion(ILogger logger, MembershipVersion version);

        [LoggerMessage(8, LogLevel.Debug, "Relinquishing ownership of range '{Range}' at version '{Version}'.")]
        public static partial void RelinquishingOwnership(ILogger logger, RingRange range, MembershipVersion version);

        [LoggerMessage(9, LogLevel.Debug, "Encountered non-contiguous update from '{Previous}' to '{Current}' while releasing range '{Range}'. Dropping snapshot.")]
        public static partial void EncounteredNonContiguousUpdate(ILogger logger, MembershipVersion previous, MembershipVersion current, RingRange range);

        [LoggerMessage(10, LogLevel.Debug, "No transfer partners for snapshot of range '{Range}' at version '{Version}'. Dropping snapshot.")]
        public static partial void NoTransferPartners(ILogger logger, RingRange range, MembershipVersion version);

        [LoggerMessage(11, LogLevel.Debug, "Acquiring range '{Range}'.")]
        public static partial void AcquiringRange(ILogger logger, RingRange range);

        [LoggerMessage(12, LogLevel.Debug, "Non-contiguous view change detected: '{PreviousVersion}' to '{CurrentVersion}'. Performing recovery.")]
        public static partial void NonContiguousViewChange(ILogger logger, MembershipVersion previousVersion, MembershipVersion currentVersion, RingRange range);

        [LoggerMessage(13, LogLevel.Debug, "Completed transferring entries for range '{Range}' at version '{Version}' took {Elapsed}ms.{Recovered}")]
        public static partial void CompletedTransferringEntries(ILogger logger, RingRange range, MembershipVersion version, long elapsed, bool recovered);

        [LoggerMessage(14, LogLevel.Trace, "Requesting entries for ranges '{Range}' from '{PreviousOwner}' at version '{PreviousVersion}'.")]
        public static partial void RequestingEntries(ILogger logger, RingRange range, SiloAddress previousOwner, MembershipVersion previousVersion);

        [LoggerMessage(15, LogLevel.Warning, "Expected a valid snapshot from previous owner '{PreviousOwner}' for part of ranges '{Range}', but found none.")]
        public static partial void ExpectedValidSnapshot(ILogger logger, SiloAddress previousOwner, RingRange range);

        [LoggerMessage(16, LogLevel.Trace, "Received '{Entry}' via snapshot from '{PreviousOwner}' for version '{Version}'.")]
        public static partial void ReceivedEntryViaSnapshot(ILogger logger, GrainAddress entry, SiloAddress previousOwner, MembershipVersion version);

        [LoggerMessage(17, LogLevel.Debug, "Transferred '{Count}' entries for range '{Range}' from '{PreviousOwner}'.")]
        public static partial void TransferredEntries(ILogger logger, int count, RingRange range, SiloAddress previousOwner);

        [LoggerMessage(18, LogLevel.Warning, "Remote host became unavailable while transferring ownership of range '{Range}'. Recovery will be performed.")]
        public static partial void RemoteHostUnavailable(ILogger logger, RingRange range);

        [LoggerMessage(19, LogLevel.Warning, "Error transferring ownership of range '{Range}'. Recovery will be performed.")]
        public static partial void ErrorTransferringOwnership(ILogger logger, Exception exception, RingRange range);

        [LoggerMessage(20, LogLevel.Debug, "Recovering activations from range '{Range}' at version '{Version}'.")]
        public static partial void RecoveringActivations(ILogger logger, RingRange range, MembershipVersion version);

        [LoggerMessage(21, LogLevel.Trace, "Recovered '{Entry}' for version '{Version}'.")]
        public static partial void RecoveredEntry(ILogger logger, GrainAddress entry, MembershipVersion version);

        [LoggerMessage(22, LogLevel.Debug, "Completed recovering activations from range '{Range}' at version '{Version}' took '{Elapsed}'.")]
        public static partial void CompletedRecoveringActivations(ILogger logger, RingRange range, MembershipVersion version, TimeSpan elapsed);

        [LoggerMessage(23, LogLevel.Debug, "Recovered '{Count}' entries from silo '{SiloAddress}' for ranges '{Range}' at version '{Version}' in {ElapsedMilliseconds}ms.")]
        public static partial void RecoveredEntries(ILogger logger, int count, SiloAddress siloAddress, RingRange range, MembershipVersion version, long elapsedMilliseconds);

        [LoggerMessage(24, LogLevel.Error, "Error invoking operation '{Operation}' on silo '{SiloAddress}'.")]
        public static partial void ErrorInvokingOperation(ILogger logger, Exception exception, string operation, SiloAddress siloAddress);

        [LoggerMessage(25, LogLevel.Error, "Integrity violation: Recovered entry '{RecoveredRecord}' does not match existing entry '{LocalRecord}'.")]
        public static partial void IntegrityViolationRecoveredEntry(ILogger logger, GrainAddress recoveredRecord, GrainAddress localRecord);

        [LoggerMessage(26, LogLevel.Error, "Integrity violation: Recovered entry '{RecoveredRecord}' not found in directory.")]
        public static partial void IntegrityViolationRecoveredEntryNotFound(ILogger logger, GrainAddress recoveredRecord);
    }
}
