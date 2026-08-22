using System.Collections.Immutable;
using Orleans.Configuration;

namespace Orleans.Runtime.Dissemination;

// A namespace owns current state plus any history and caching needed to repair a peer from an acknowledged version.
internal interface IDisseminationNamespace
{
    DisseminationNamespace Name { get; }

    DisseminationNamespaceOptions Options { get; }

    IEnumerable<DigestEntry> Digests { get; }

    long GetVersion(DisseminationKey key);

    DisseminationRepairResult CreateRepair(in DisseminationRepairRequest request);

    ValueTask<DisseminationApplyResult> ApplyValueAsync(
        DisseminationValue value,
        CancellationToken cancellationToken);
}

// A null FromVersion means no known peer baseline; a null ToVersion asks for the highest repairable version.
internal readonly struct DisseminationRepairRequest(
    DisseminationKey key,
    long? fromVersion,
    long? toVersion,
    int maxItemCount,
    int maxBatchBytes,
    int maxPayloadBytes)
{
    public DisseminationKey Key { get; } = key;

    public long? FromVersion { get; } = fromVersion;

    public long? ToVersion { get; } = toVersion;

    public int MaxItemCount { get; } = maxItemCount;

    public int MaxBatchBytes { get; } = maxBatchBytes;

    public int MaxPayloadBytes { get; } = maxPayloadBytes;
}

// Version reports the namespace's resolved or current version.
// For Produced results, IsComplete says whether Values reaches that version or only forms a prefix.
internal readonly struct DisseminationRepairResult(
    DisseminationRepairStatus status,
    long version,
    ImmutableArray<DisseminationValue> values,
    bool isComplete)
{
    public DisseminationRepairStatus Status { get; } = status;

    public long Version { get; } = version;

    public ImmutableArray<DisseminationValue> Values { get; } = values.IsDefault ? [] : values;

    public bool IsComplete { get; } = isComplete;

    public static DisseminationRepairResult Current(long version) =>
        new(DisseminationRepairStatus.Current, version, [], isComplete: true);

    public static DisseminationRepairResult Produced(
        long version,
        ImmutableArray<DisseminationValue> values,
        bool isComplete = true) =>
        new(DisseminationRepairStatus.Produced, version, values, isComplete);

    public static DisseminationRepairResult Unavailable(long version) =>
        new(DisseminationRepairStatus.Unavailable, version, [], isComplete: false);

    public static DisseminationRepairResult InsufficientCapacity(long version) =>
        new(DisseminationRepairStatus.InsufficientCapacity, version, [], isComplete: false);
}

internal enum DisseminationRepairStatus
{
    // The peer is already at or beyond the resolved version.
    Current,
    // Values contains a valid repair, possibly a prefix when IsComplete is false.
    Produced,
    // The key or requested target cannot currently be reconstructed.
    Unavailable,
    // No valid repair fits within the supplied item or byte budget.
    InsufficientCapacity,
}
