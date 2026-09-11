using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.Serialization;

namespace Orleans.Runtime.Dissemination;

// Membership retains a bounded snapshot history so each peer can receive either a compact diff
// or a universal full snapshot.
internal sealed class MembershipDisseminationNamespace(
    IMembershipManager membershipManager,
    IOptionsMonitor<ClusterMembershipOptions> options,
    Serializer serializer) : IDisseminationNamespace
{
    private const int MaxSnapshotHistory = 32;
    private readonly object _historyLock = new();
    private readonly SortedDictionary<long, MembershipTableSnapshot> _snapshotHistory = new();
    private readonly Dictionary<long, ReadOnlyMemory<byte>> _snapshotPayloads = [];
    private readonly Dictionary<(long FromVersion, long ToVersion), ReadOnlyMemory<byte>> _diffPayloads = [];

    public DisseminationNamespace Name => DisseminationNamespaceNames.Membership;

    public DisseminationMembershipScope MembershipScope => DisseminationMembershipScope.AllMembers;

    public DisseminationNamespaceOptions Options => options.CurrentValue.Dissemination;

    public async ValueTask<bool> PublishAsync(
        IDisseminationService disseminationService,
        MembershipTableSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Remember the exact snapshot before waking peer pumps so it is immediately repairable.
        RememberSnapshot(snapshot);
        return await disseminationService.Publish(
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
            RememberSnapshot(snapshot);
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

        // Select history and cached bytes atomically because membership can change without advancing its version.
        lock (_historyLock)
        {
            var currentSnapshot = membershipManager.CurrentSnapshot;
            RememberSnapshotUnsafe(currentSnapshot);
            var targetVersion = request.ToVersion ?? currentSnapshot.Version.Value;
            if (targetVersion > currentSnapshot.Version.Value
                || !_snapshotHistory.TryGetValue(targetVersion, out var targetSnapshot))
            {
                return DisseminationRepairResult.Unavailable(currentSnapshot.Version.Value);
            }

            // Prefer a retained peer baseline when available; otherwise the full snapshot remains the fallback.
            MembershipTableSnapshot? baseSnapshot = null;
            if (request.FromVersion is { } fromVersion
                && fromVersion > 0
                && fromVersion < targetVersion)
            {
                _snapshotHistory.TryGetValue(fromVersion, out baseSnapshot);
            }

            var resolvedVersion = targetSnapshot.Version.Value;
            if (request.FromVersion is { } peerVersion && peerVersion > resolvedVersion)
            {
                return DisseminationRepairResult.Current(resolvedVersion);
            }

            if (request.MaxItemCount <= 0)
            {
                return DisseminationRepairResult.InsufficientCapacity(resolvedVersion);
            }

            var snapshotValue = CreateSnapshotValue(targetSnapshot);
            var selectedValue = snapshotValue;
            if (baseSnapshot is not null)
            {
                // Use the smaller representation, retaining the full snapshot as the capacity fallback.
                var diffValue = CreateDiffValue(baseSnapshot, targetSnapshot);
                if (diffValue.Payload.Length < snapshotValue.Payload.Length)
                {
                    selectedValue = diffValue;
                }
            }

            if (selectedValue.Payload.Length > request.MaxPayloadBytes
                || selectedValue.Payload.Length > request.MaxBatchBytes)
            {
                if (selectedValue.FromVersion != 0
                    && snapshotValue.Payload.Length <= request.MaxPayloadBytes
                    && snapshotValue.Payload.Length <= request.MaxBatchBytes)
                {
                    selectedValue = snapshotValue;
                }
                else
                {
                    return DisseminationRepairResult.InsufficientCapacity(resolvedVersion);
                }
            }

            return DisseminationRepairResult.Produced(resolvedVersion, [selectedValue]);
        }
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

        if (update.Diff is { } diff)
        {
            return update.Snapshot is null
                && value.FromVersion == diff.BaseVersion.Value
                && value.ToVersion == diff.Version.Value
                ? await ApplyDiff(diff, cancellationToken)
                : DisseminationApplyResult.Rejected;
        }

        if (update.Snapshot is not { } snapshot)
        {
            return DisseminationApplyResult.Rejected;
        }

        if (value.FromVersion != 0 || value.ToVersion != snapshot.Version.Value)
        {
            return DisseminationApplyResult.Rejected;
        }

        var currentSnapshot = membershipManager.CurrentSnapshot;
        if (snapshot.Version < currentSnapshot.Version)
        {
            return DisseminationApplyResult.Obsolete;
        }

        if (snapshot.Version == currentSnapshot.Version)
        {
            // Same-version snapshots can still advance IAmAlive state.
            if (!snapshot.IsSuccessorTo(currentSnapshot))
            {
                return DisseminationApplyResult.Duplicate;
            }

            await membershipManager.ProcessGossipSnapshot(snapshot, cancellationToken);
            RememberSnapshot(membershipManager.CurrentSnapshot);
            return DisseminationApplyResult.Applied;
        }

        await membershipManager.ProcessGossipSnapshot(snapshot, cancellationToken);
        RememberSnapshot(membershipManager.CurrentSnapshot);
        return DisseminationApplyResult.Applied;
    }

    private DisseminationValue CreateSnapshotValue(MembershipTableSnapshot snapshot) => new(
        DisseminationKey.Default,
        fromVersion: 0,
        toVersion: snapshot.Version.Value,
        GetSnapshotPayload(snapshot));

    private DisseminationValue CreateDiffValue(
        MembershipTableSnapshot baseSnapshot,
        MembershipTableSnapshot snapshot) => new(
        DisseminationKey.Default,
        fromVersion: baseSnapshot.Version.Value,
        toVersion: snapshot.Version.Value,
        GetDiffPayload(baseSnapshot, snapshot));

    private async ValueTask<DisseminationApplyResult> ApplyDiff(MembershipTableSnapshotDiff diff, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = membershipManager.CurrentSnapshot;
        if (current.Version.Value > diff.Version.Value)
        {
            return DisseminationApplyResult.Obsolete;
        }

        if (current.Version.Value != diff.Version.Value
            && current.Version.Value != diff.BaseVersion.Value)
        {
            return DisseminationApplyResult.Rejected;
        }

        var entries = current.Entries.ToBuilder();
        foreach (var silo in diff.RemovedSilos)
        {
            entries.Remove(silo);
        }

        foreach (var entry in diff.UpdatedEntries)
        {
            entries[entry.SiloAddress] = PreserveIAmAliveTime(current, entry);
        }

        var snapshot = new MembershipTableSnapshot(diff.Version, entries.ToImmutable());
        if (current.Version.Value == diff.Version.Value && !snapshot.IsSuccessorTo(current))
        {
            return DisseminationApplyResult.Duplicate;
        }

        await membershipManager.ProcessGossipSnapshot(snapshot, cancellationToken);
        RememberSnapshot(membershipManager.CurrentSnapshot);
        return DisseminationApplyResult.Applied;
    }

    private void RememberSnapshot(MembershipTableSnapshot snapshot)
    {
        lock (_historyLock)
        {
            RememberSnapshotUnsafe(snapshot);
        }
    }

    private void RememberSnapshotUnsafe(MembershipTableSnapshot snapshot)
    {
        if (_snapshotHistory.TryGetValue(snapshot.Version.Value, out var previous)
            && !MembershipSnapshotsEqual(previous, snapshot))
        {
            // Replacing a same-version snapshot invalidates bytes derived from the older liveness state.
            InvalidatePayloads(snapshot.Version.Value);
        }

        _snapshotHistory[snapshot.Version.Value] = snapshot;
        while (_snapshotHistory.Count > MaxSnapshotHistory)
        {
            using var enumerator = _snapshotHistory.Keys.GetEnumerator();
            if (enumerator.MoveNext())
            {
                var removedVersion = enumerator.Current;
                _snapshotHistory.Remove(removedVersion);
                InvalidatePayloads(removedVersion);
            }
        }
    }

    private void InvalidatePayloads(long version)
    {
        _snapshotPayloads.Remove(version);
        foreach (var key in _diffPayloads.Keys
            .Where(key => key.FromVersion == version || key.ToVersion == version)
            .ToArray())
        {
            _diffPayloads.Remove(key);
        }
    }

    private ReadOnlyMemory<byte> GetSnapshotPayload(MembershipTableSnapshot snapshot)
    {
        lock (_historyLock)
        {
            if (!_snapshotPayloads.TryGetValue(snapshot.Version.Value, out var payload))
            {
                payload = serializer.SerializeToArray(new MembershipTableSnapshotUpdate { Snapshot = snapshot });
                _snapshotPayloads.Add(snapshot.Version.Value, payload);
            }

            return payload;
        }
    }

    private ReadOnlyMemory<byte> GetDiffPayload(
        MembershipTableSnapshot baseSnapshot,
        MembershipTableSnapshot snapshot)
    {
        var key = (baseSnapshot.Version.Value, snapshot.Version.Value);
        lock (_historyLock)
        {
            if (!_diffPayloads.TryGetValue(key, out var payload))
            {
                payload = serializer.SerializeToArray(new MembershipTableSnapshotUpdate
                {
                    Diff = CreateDiff(baseSnapshot, snapshot),
                });
                _diffPayloads.Add(key, payload);
            }

            return payload;
        }
    }

    private static MembershipTableSnapshotDiff CreateDiff(MembershipTableSnapshot baseSnapshot, MembershipTableSnapshot snapshot)
    {
        // Include every current entry: peers at the same table version can still have different liveness baselines.
        var updated = ImmutableArray.CreateBuilder<MembershipEntry>();
        foreach (var entry in snapshot.Entries)
        {
            updated.Add(entry.Value);
        }

        var removed = ImmutableArray.CreateBuilder<SiloAddress>();
        foreach (var entry in baseSnapshot.Entries)
        {
            if (!snapshot.Entries.ContainsKey(entry.Key))
            {
                removed.Add(entry.Key);
            }
        }

        return new MembershipTableSnapshotDiff(
            baseSnapshot.Version,
            snapshot.Version,
            updated.ToImmutable(),
            removed.ToImmutable());
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
            hash = unchecked((hash ^ (ulong)entry.Value.EffectiveIAmAliveTime.Ticks) * prime);
        }

        return unchecked((long)hash);
    }

    private static MembershipEntry PreserveIAmAliveTime(MembershipTableSnapshot previousSnapshot, MembershipEntry entry)
    {
        // A repair must not move locally observed liveness backward.
        if (previousSnapshot.Entries.TryGetValue(entry.SiloAddress, out var previousEntry)
            && previousEntry.IAmAliveTime > entry.IAmAliveTime)
        {
            return CopyWithIAmAliveTime(entry, previousEntry.IAmAliveTime);
        }

        return entry;
    }

    private static MembershipEntry CopyWithIAmAliveTime(MembershipEntry entry, DateTime iAmAliveTime) => new()
    {
        SiloAddress = entry.SiloAddress,
        Status = entry.Status,
        SuspectTimes = entry.SuspectTimes is null ? null : new(entry.SuspectTimes),
        ProxyPort = entry.ProxyPort,
        HostName = entry.HostName,
        SiloName = entry.SiloName,
        RoleName = entry.RoleName,
        UpdateZone = entry.UpdateZone,
        FaultZone = entry.FaultZone,
        StartTime = entry.StartTime,
        IAmAliveTime = iAmAliveTime,
    };

    private static bool MembershipEntriesEqual(MembershipEntry left, MembershipEntry right) =>
        left.SiloAddress.Equals(right.SiloAddress)
        && left.Status == right.Status
        && EqualSuspectTimes(left.SuspectTimes, right.SuspectTimes)
        && left.ProxyPort == right.ProxyPort
        && string.Equals(left.HostName, right.HostName, StringComparison.Ordinal)
        && string.Equals(left.SiloName, right.SiloName, StringComparison.Ordinal)
        && string.Equals(left.RoleName, right.RoleName, StringComparison.Ordinal)
        && left.UpdateZone == right.UpdateZone
        && left.FaultZone == right.FaultZone
        && left.StartTime == right.StartTime
        && left.IAmAliveTime == right.IAmAliveTime;

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
                || !MembershipEntriesEqual(entry, other))
            {
                return false;
            }
        }

        return true;
    }

    private static bool EqualSuspectTimes(List<Tuple<SiloAddress, DateTime>>? left, List<Tuple<SiloAddress, DateTime>>? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!Equals(left[i].Item1, right[i].Item1) || left[i].Item2 != right[i].Item2)
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
    public MembershipTableSnapshotDiff? Diff { get; init; }
}

[GenerateSerializer, Immutable]
internal sealed class MembershipTableSnapshotDiff
{
    public MembershipTableSnapshotDiff(
        MembershipVersion baseVersion,
        MembershipVersion version,
        ImmutableArray<MembershipEntry> updatedEntries,
        ImmutableArray<SiloAddress> removedSilos)
    {
        BaseVersion = baseVersion;
        Version = version;
        UpdatedEntries = updatedEntries;
        RemovedSilos = removedSilos;
    }

    [Id(0)]
    public MembershipVersion BaseVersion { get; }

    [Id(1)]
    public MembershipVersion Version { get; }

    [Id(2)]
    public ImmutableArray<MembershipEntry> UpdatedEntries { get; }

    [Id(3)]
    public ImmutableArray<SiloAddress> RemovedSilos { get; }
}
