using System.Collections.Immutable;
using Orleans.Configuration;

namespace Orleans.Runtime.Dissemination;

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
    Current,
    Produced,
    Unavailable,
    InsufficientCapacity,
}
