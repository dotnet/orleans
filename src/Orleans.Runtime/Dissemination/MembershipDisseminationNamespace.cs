using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.Serialization;

namespace Orleans.Runtime.Dissemination;

// Full snapshots preserve the complete inventory and same-version heartbeat advances.
internal sealed class MembershipDisseminationNamespace(
    IMembershipManager membershipManager,
    IOptionsMonitor<ClusterMembershipOptions> options,
    Serializer serializer) : IDisseminationNamespace
{
    private static readonly DisseminationKey[] MembershipKeys = [DisseminationKey.Default];
    private readonly object _cacheLock = new();
    private MembershipTableSnapshot? _cachedSnapshot;
    private byte[]? _cachedPayload;

    public DisseminationNamespace Name => DisseminationNamespaceNames.Membership;

    public DisseminationMembershipScope MembershipScope => DisseminationMembershipScope.AllMembers;

    public bool ValidateOlderFullValues => true;

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
            var snapshot = membershipManager.CurrentSnapshot;
            var version = snapshot.Version.Value;
            if (_cachedSnapshot is null || !MembershipSnapshotsEqual(_cachedSnapshot, snapshot))
            {
                _cachedPayload = null;
            }

            _cachedSnapshot = snapshot;
            _cachedPayload ??= serializer.SerializeToArray(new MembershipTableSnapshotUpdate { Snapshot = snapshot });
            return _cachedPayload.Length <= request.MaxPayloadBytes && _cachedPayload.Length <= request.MaxBatchBytes
                ? DisseminationRepairResult.Produced(new DisseminationValue(DisseminationKey.Default, 0, version, _cachedPayload))
                : DisseminationRepairResult.InsufficientCapacity(version);
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

        if (value.FromVersion != 0
            || serializer.Deserialize<MembershipTableSnapshotUpdate>(value.Payload)?.Snapshot is not { } snapshot
            || value.ToVersion != snapshot.Version.Value)
        {
            return DisseminationApplyResult.Rejected;
        }

        // The membership manager merges maximum per-entry IAmAliveTime before publishing a full snapshot.
        var currentSnapshot = membershipManager.CurrentSnapshot;
        if (snapshot.Version == currentSnapshot.Version)
        {
            // Same-version snapshots can advance liveness or remove inactive entries.
            if (!snapshot.IsSuccessorTo(currentSnapshot))
            {
                return DisseminationApplyResult.Duplicate;
            }
        }

        await membershipManager.ProcessGossipSnapshot(snapshot, cancellationToken);
        var result = MembershipSnapshotsEqual(currentSnapshot, membershipManager.CurrentSnapshot)
            ? DisseminationApplyResult.Duplicate
            : DisseminationApplyResult.Applied;
        return snapshot.Version < currentSnapshot.Version && result == DisseminationApplyResult.Duplicate
            ? DisseminationApplyResult.Obsolete
            : result;
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

    private static bool MembershipEntriesEqual(MembershipEntry left, MembershipEntry right) =>
        MembershipTableSnapshot.AreVersionedFieldsEqual(left, right)
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
}

[GenerateSerializer, Immutable]
internal sealed class MembershipTableSnapshotUpdate
{
    [Id(0)]
    public MembershipTableSnapshot? Snapshot { get; init; }
}
