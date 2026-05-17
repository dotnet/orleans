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

    public static JournalId StoragePrefix => JournalId.Create(RootSegment, ShardsSegment);

    public static JobShardId New() => new(Guid.NewGuid().ToString("N"));

    public static JobShardId Parse(string value) => new(value);

    public static JobShardId FromJournalId(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        var segments = DecodeSegments(journalId.Value);
        if (segments.Length != 3
            || !string.Equals(segments[0], RootSegment, StringComparison.Ordinal)
            || !string.Equals(segments[1], ShardsSegment, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Journal id '{journalId}' is not a DurableJobs shard journal id.", nameof(journalId));
        }

        return new(segments[2]);
    }

    public JournalId ToJournalId() => JournalId.Create(RootSegment, ShardsSegment, Value);

    public override string ToString() => Value;

    private static string[] DecodeSegments(string value)
    {
        if (value[0] == '/' || value[^1] == '/')
        {
            throw new ArgumentException("A journal id must not start or end with a separator.", nameof(value));
        }

        var encodedSegments = value.Split('/');
        var decodedSegments = new string[encodedSegments.Length];
        for (var i = 0; i < encodedSegments.Length; i++)
        {
            if (encodedSegments[i].Length == 0)
            {
                throw new ArgumentException("A journal id must not contain empty segments.", nameof(value));
            }

            decodedSegments[i] = Uri.UnescapeDataString(encodedSegments[i]);
        }

        return decodedSegments;
    }
}
