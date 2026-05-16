using System;
using Orleans.Journaling;

namespace Orleans.DurableJobs;

internal readonly record struct JobShardId
{
    private const string RootSegment = "durable-jobs";
    private const string ShardsSegment = "shards";

    public JobShardId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public static JournalStoragePrefix StoragePrefix => JournalStoragePrefix.Create(RootSegment, ShardsSegment);

    public static JobShardId New() => new(Guid.NewGuid().ToString("N"));

    public static JobShardId Parse(string value) => new(value);

    public static JobShardId FromJournalStorageId(JournalStorageId storageId)
    {
        ArgumentNullException.ThrowIfNull(storageId);

        var segments = storageId.Segments;
        if (segments.Count != 3
            || !string.Equals(segments[0], RootSegment, StringComparison.Ordinal)
            || !string.Equals(segments[1], ShardsSegment, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Journal storage id '{storageId}' is not a DurableJobs shard storage id.", nameof(storageId));
        }

        return new(segments[2]);
    }

    public JournalStorageId ToJournalStorageId() => JournalStorageId.Create(RootSegment, ShardsSegment, Value);

    public override string ToString() => Value;
}
