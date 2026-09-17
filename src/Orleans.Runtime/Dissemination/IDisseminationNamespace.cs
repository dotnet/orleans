using Orleans.Configuration;

namespace Orleans.Runtime.Dissemination;

// A namespace owns current state, serialized payload caching, and repair construction.
internal interface IDisseminationNamespace
{
    DisseminationNamespace Name { get; }

    DisseminationMembershipScope MembershipScope => DisseminationMembershipScope.AllMembers;

    DisseminationRoutingMode RoutingMode => DisseminationRoutingMode.BroadcastTree;

    TimeSpan AggregationPeriod => TimeSpan.FromSeconds(1);

    // Full values can be authority-refresh hints even when their numeric version is older.
    bool ValidateOlderFullValues => false;

    DisseminationNamespaceOptions Options { get; }

    IEnumerable<DigestEntry> Digests { get; }

    // Inventory maintenance needs identities, independently of versions and anti-entropy fingerprints.
    IEnumerable<DisseminationKey> Keys
    {
        get
        {
            foreach (var digest in Digests)
            {
                yield return digest.Key;
            }
        }
    }

    long GetVersion(DisseminationKey key);

    DisseminationRepairResult CreateRepair(in DisseminationRepairRequest request);

    ValueTask<DisseminationApplyResult> ApplyValueAsync(
        DisseminationValue value,
        CancellationToken cancellationToken);
}

internal enum DisseminationMembershipScope
{
    ActiveMembers,
    AllMembers,
}

internal enum DisseminationRoutingMode
{
    BroadcastTree,
    AggregationTree,
}

// A null FromVersion means no known peer baseline. Repairs materialize the current full value.
internal readonly struct DisseminationRepairRequest(
    DisseminationKey key,
    long? fromVersion,
    int maxBatchBytes,
    int maxPayloadBytes)
{
    public DisseminationKey Key { get; } = key;

    public long? FromVersion { get; } = fromVersion;

    public int MaxBatchBytes { get; } = maxBatchBytes;

    public int MaxPayloadBytes { get; } = maxPayloadBytes;
}

// Version reports the current namespace version; a Produced result carries its full value.
internal readonly struct DisseminationRepairResult(
    DisseminationRepairStatus status,
    long version,
    DisseminationValue value)
{
    public DisseminationRepairStatus Status { get; } = status;

    public long Version { get; } = version;

    public DisseminationValue Value { get; } = value;

    public static DisseminationRepairResult Current(long version) =>
        new(DisseminationRepairStatus.Current, version, default);

    public static DisseminationRepairResult Produced(DisseminationValue value) =>
        new(DisseminationRepairStatus.Produced, value.ToVersion, value);

    public static DisseminationRepairResult Unavailable(long version) =>
        new(DisseminationRepairStatus.Unavailable, version, default);

    public static DisseminationRepairResult InsufficientCapacity(long version) =>
        new(DisseminationRepairStatus.InsufficientCapacity, version, default);
}

internal enum DisseminationRepairStatus
{
    // The peer is already at or beyond the resolved version.
    Current,
    // Value contains the current full value.
    Produced,
    // The key has no current value.
    Unavailable,
    // The current value exceeds the supplied byte budget.
    InsufficientCapacity,
}
