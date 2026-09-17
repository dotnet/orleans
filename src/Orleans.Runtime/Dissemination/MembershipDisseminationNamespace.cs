using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.Serialization;

namespace Orleans.Runtime.Dissemination;

// Broadcasts compare immutable snapshots; repair carries the complete current inventory.
internal sealed class MembershipDisseminationNamespace(
    IMembershipManager membershipManager,
    IOptionsMonitor<ClusterMembershipOptions> options,
    Serializer serializer) : IDisseminationNamespace
{
    private static readonly DisseminationKey[] MembershipKeys = [DisseminationKey.Default];
    private readonly object _cacheLock = new();
    private BroadcastState? _cachedState;
    private byte[]? _cachedPayload;
    private BroadcastState? _cachedBroadcastBase;
    private DisseminationValue _cachedBroadcast;

    public DisseminationNamespace Name => DisseminationNamespaceNames.Membership;

    public DisseminationMembershipScope MembershipScope => DisseminationMembershipScope.AllMembers;

    public bool BroadcastsAreDeltas => true;

    public DisseminationNamespaceOptions Options => options.CurrentValue.Dissemination;

    public IEnumerable<DisseminationKey> Keys => MembershipKeys;

    public ValueTask<bool> PublishAsync(
        IDisseminationService disseminationService,
        MembershipTableSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return disseminationService.Publish(
            this,
            DisseminationKey.Default,
            snapshot.Version.Value,
            cancellationToken);
    }

    public IEnumerable<DigestEntry> Digests
    {
        get
        {
            var snapshot = membershipManager.CurrentSnapshot;
            // Version alone misses same-version liveness advances, so the digest fingerprints heartbeat state too.
            yield return new DigestEntry(
                DisseminationKey.Default,
                snapshot.Version.Value,
                GetFingerprint(snapshot));
        }
    }

    public long GetVersion(DisseminationKey key) =>
        key == DisseminationKey.Default
            ? membershipManager.CurrentSnapshot.Version.Value
            : 0;

    public DisseminationRepairResult CreateRepair(in DisseminationRepairRequest request)
    {
        if (request.Key != DisseminationKey.Default)
        {
            return DisseminationRepairResult.Unavailable(version: 0);
        }

        lock (_cacheLock)
        {
            // Re-read the owner under the cache lock: publication arguments can outlive the state they describe.
            var state = GetCurrentState();
            var version = state.Version;
            _cachedPayload ??= serializer.SerializeToArray(new MembershipTableSnapshotUpdate { Snapshot = state.Snapshot });
            return _cachedPayload.Length <= request.MaxPayloadBytes && _cachedPayload.Length <= request.MaxBatchBytes
                ? DisseminationRepairResult.Produced(new DisseminationValue(DisseminationKey.Default, 0, version, _cachedPayload))
                : DisseminationRepairResult.InsufficientCapacity(version);
        }
    }

    public DisseminationRepairResult CreateBroadcast(
        in DisseminationRepairRequest request,
        DisseminationBroadcastState? baseline)
    {
        if (request.Key != DisseminationKey.Default)
        {
            return DisseminationRepairResult.Unavailable(version: 0);
        }

        lock (_cacheLock)
        {
            var current = GetCurrentState();
            if (current.Version <= 0)
            {
                return DisseminationRepairResult.Unavailable(current.Version);
            }

            if (request.FromVersion > current.Version)
            {
                return DisseminationRepairResult.Current(current.Version);
            }

            // An empty current-view delta establishes a comparison baseline. Missing views use full repair.
            var previous = baseline is null ? current : (BroadcastState)baseline;
            if (previous.Version > current.Version)
            {
                return DisseminationRepairResult.Current(current.Version);
            }

            if (!ReferenceEquals(_cachedBroadcastBase, previous))
            {
                var updated = ImmutableArray.CreateBuilder<MembershipEntry>();
                foreach (var (silo, entry) in current.Snapshot.Entries)
                {
                    if (!previous.Snapshot.Entries.TryGetValue(silo, out var old)
                        || !MembershipEntriesEqual(old, entry))
                    {
                        updated.Add(entry);
                    }
                }

                var removed = ImmutableArray.CreateBuilder<SiloAddress>();
                foreach (var silo in previous.Snapshot.Entries.Keys)
                {
                    if (!current.Snapshot.Entries.ContainsKey(silo))
                    {
                        removed.Add(silo);
                    }
                }

                var delta = new MembershipTableSnapshotDelta(
                    previous.Snapshot.Version, current.Snapshot.Version,
                    updated.ToImmutable(), removed.ToImmutable());
                _cachedBroadcast = new(
                    DisseminationKey.Default, previous.Version, current.Version,
                    serializer.SerializeToArray(new MembershipTableSnapshotUpdate { Delta = delta }));
                _cachedBroadcastBase = previous;
            }

            return _cachedBroadcast.Payload.Length <= request.MaxPayloadBytes
                && _cachedBroadcast.Payload.Length <= request.MaxBatchBytes
                ? DisseminationRepairResult.Produced(_cachedBroadcast, current)
                : DisseminationRepairResult.InsufficientCapacity(current.Version);
        }
    }

    private BroadcastState GetCurrentState()
    {
        var snapshot = membershipManager.CurrentSnapshot;
        if (_cachedState is null || !MembershipSnapshotsEqual(_cachedState.Snapshot, snapshot))
        {
            _cachedState = new(snapshot);
            _cachedPayload = null;
            _cachedBroadcastBase = null;
            _cachedBroadcast = default;
        }

        return _cachedState;
    }

    public async ValueTask<DisseminationApplyResult> ApplyValueAsync(
        DisseminationValue value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value.Key != DisseminationKey.Default)
        {
            return DisseminationApplyResult.Rejected;
        }

        if (serializer.Deserialize<MembershipTableSnapshotUpdate>(value.Payload) is not { } update)
        {
            return DisseminationApplyResult.Rejected;
        }

        var previous = membershipManager.CurrentSnapshot;
        MembershipTableSnapshot? snapshot;
        IEnumerable<MembershipEntry> heartbeats;
        IEnumerable<SiloAddress> removed;
        if (update.Delta is { } delta)
        {
            if (update.Snapshot is not null
                || value.FromVersion != delta.BaseVersion.Value
                || value.ToVersion != delta.Version.Value
                || delta.BaseVersion.Value <= 0
                || delta.Version < delta.BaseVersion
                || delta.UpdatedEntries.IsDefault || delta.RemovedSilos.IsDefault)
            {
                return DisseminationApplyResult.Rejected;
            }

            if (delta.Version < previous.Version)
            {
                return DisseminationApplyResult.Obsolete;
            }

            if (previous.Version != delta.BaseVersion && previous.Version != delta.Version)
            {
                return DisseminationApplyResult.Rejected;
            }

            snapshot = ApplyDelta(previous, delta);
            heartbeats = delta.UpdatedEntries;
            removed = delta.RemovedSilos;
        }
        else if (update.Snapshot is { } full
            && value.FromVersion == 0
            && value.ToVersion == full.Version.Value)
        {
            snapshot = full.Version == previous.Version ? MergeSameVersion(previous, full) : full;
            heartbeats = full.Entries.Values;
            removed = full.Version == previous.Version
                ? previous.Entries.Keys.Where(silo => !full.Entries.ContainsKey(silo))
                : [];
        }
        else
        {
            return DisseminationApplyResult.Rejected;
        }

        if (snapshot is null)
        {
            return DisseminationApplyResult.Rejected;
        }

        if (MembershipSnapshotsEqual(previous, snapshot) && CoversChanges(previous, heartbeats, removed))
        {
            return DisseminationApplyResult.Duplicate;
        }

        await membershipManager.ProcessGossipSnapshot(snapshot, cancellationToken);
        var current = membershipManager.CurrentSnapshot;
        if (snapshot.Version < current.Version)
        {
            return DisseminationApplyResult.Obsolete;
        }

        // A concurrent authority refresh can publish while the gossip call waits. Confirm the requested
        // effects, rather than treating any unrelated owner change as acceptance of this update.
        if (current.Version != snapshot.Version || !CoversChanges(current, heartbeats, removed))
        {
            return DisseminationApplyResult.Rejected;
        }

        return MembershipSnapshotsEqual(previous, current)
            ? DisseminationApplyResult.Duplicate
            : DisseminationApplyResult.Applied;
    }

    private static MembershipTableSnapshot? ApplyDelta(
        MembershipTableSnapshot current,
        MembershipTableSnapshotDelta delta)
    {
        var entries = current.Entries.ToBuilder();
        var touched = new HashSet<SiloAddress>();
        var sameVersion = current.Version == delta.Version;
        foreach (var entry in delta.UpdatedEntries)
        {
            if (entry?.SiloAddress is not { } silo || !touched.Add(silo))
            {
                return null;
            }

            if (sameVersion)
            {
                if (entries.TryGetValue(silo, out var existing))
                {
                    entries[silo] = MergeHeartbeat(existing, entry);
                }
                else if (entry.Status != SiloStatus.Dead)
                {
                    return null;
                }
            }
            else
            {
                entries[silo] = entries.TryGetValue(silo, out var existing)
                    ? MergeHeartbeat(entry, existing)
                    : entry;
            }
        }

        foreach (var silo in delta.RemovedSilos)
        {
            if (silo is null || !touched.Add(silo)
                || sameVersion && entries.TryGetValue(silo, out var existing) && existing.Status != SiloStatus.Dead)
            {
                return null;
            }

            entries.Remove(silo);
        }

        return new(delta.Version, entries.ToImmutable());
    }

    private static MembershipTableSnapshot? MergeSameVersion(
        MembershipTableSnapshot current,
        MembershipTableSnapshot incoming)
    {
        var entries = current.Entries.ToBuilder();
        foreach (var (silo, entry) in current.Entries)
        {
            if (incoming.Entries.TryGetValue(silo, out var update))
            {
                entries[silo] = MergeHeartbeat(entry, update);
            }
            else if (entry.Status != SiloStatus.Dead)
            {
                return null;
            }
            else
            {
                entries.Remove(silo);
            }
        }

        foreach (var (silo, entry) in incoming.Entries)
        {
            if (entry.Status != SiloStatus.Dead && !current.Entries.ContainsKey(silo))
            {
                return null;
            }
        }

        return new(current.Version, entries.ToImmutable());
    }

    private static MembershipEntry MergeHeartbeat(MembershipEntry current, MembershipEntry incoming) =>
        incoming.IAmAliveTime > current.IAmAliveTime
            ? current.WithIAmAliveTime(incoming.IAmAliveTime)
            : current;

    private static bool CoversChanges(
        MembershipTableSnapshot current,
        IEnumerable<MembershipEntry> updated,
        IEnumerable<SiloAddress> removed)
    {
        foreach (var entry in updated)
        {
            if (current.Entries.TryGetValue(entry.SiloAddress, out var existing))
            {
                if (existing.IAmAliveTime < entry.IAmAliveTime)
                {
                    return false;
                }
            }
            else if (entry.Status != SiloStatus.Dead)
            {
                return false;
            }
        }

        return !removed.Any(current.Entries.ContainsKey);
    }

    private sealed class BroadcastState(MembershipTableSnapshot snapshot)
        : DisseminationBroadcastState(snapshot.Version.Value)
    {
        public MembershipTableSnapshot Snapshot { get; } = snapshot;
    }

    private static long GetFingerprint(MembershipTableSnapshot snapshot)
    {
        // Keep the hash deterministic across hosts and focused on state which can change without a version bump.
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var entry in snapshot.Entries.OrderBy(static entry => entry.Key))
        {
            hash = unchecked((hash ^ (uint)entry.Key.GetConsistentHashCode()) * prime);
            hash = unchecked((hash ^ (ulong)entry.Value.IAmAliveTime.Ticks) * prime);
        }

        return unchecked((long)hash);
    }

    // Comparing cross-version snapshots identifies the entries to include in a sparse broadcast.
    private static bool MembershipEntriesEqual(MembershipEntry left, MembershipEntry right) =>
        ReferenceEquals(left, right)
        || left.SiloAddress.Equals(right.SiloAddress)
        && left.Status == right.Status
        && left.ProxyPort == right.ProxyPort
        && string.Equals(left.HostName, right.HostName, StringComparison.Ordinal)
        && string.Equals(left.SiloName, right.SiloName, StringComparison.Ordinal)
        && string.Equals(left.RoleName, right.RoleName, StringComparison.Ordinal)
        && left.UpdateZone == right.UpdateZone
        && left.FaultZone == right.FaultZone
        && left.StartTime == right.StartTime
        && left.IAmAliveTime == right.IAmAliveTime
        && (ReferenceEquals(left.SuspectTimes, right.SuspectTimes)
            || left.SuspectTimes is not null && right.SuspectTimes is not null
            && left.SuspectTimes.SequenceEqual(right.SuspectTimes));

    private static bool MembershipSnapshotsEqual(
        MembershipTableSnapshot left,
        MembershipTableSnapshot right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Version != right.Version || left.Entries.Count != right.Entries.Count)
        {
            return false;
        }

        foreach (var (siloAddress, entry) in left.Entries)
        {
            if (!right.Entries.TryGetValue(siloAddress, out var other)
                || entry.IAmAliveTime != other.IAmAliveTime)
            {
                return false;
            }
        }

        return true;
    }
}

[GenerateSerializer, Immutable]
internal sealed class MembershipTableSnapshotUpdate
{
    [Id(0)]
    public MembershipTableSnapshot? Snapshot { get; init; }

    [Id(1)]
    public MembershipTableSnapshotDelta? Delta { get; init; }
}

[GenerateSerializer, Immutable]
internal sealed class MembershipTableSnapshotDelta(
    MembershipVersion baseVersion,
    MembershipVersion version,
    ImmutableArray<MembershipEntry> updatedEntries,
    ImmutableArray<SiloAddress> removedSilos)
{
    [Id(0)]
    public MembershipVersion BaseVersion { get; } = baseVersion;

    [Id(1)]
    public MembershipVersion Version { get; } = version;

    [Id(2)]
    public ImmutableArray<MembershipEntry> UpdatedEntries { get; } = updatedEntries;

    [Id(3)]
    public ImmutableArray<SiloAddress> RemovedSilos { get; } = removedSilos;
}
