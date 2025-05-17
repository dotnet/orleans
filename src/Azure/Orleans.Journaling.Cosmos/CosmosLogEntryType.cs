namespace Orleans.Journaling;

internal enum CosmosLogEntryType
{
    /// <summary>
    /// This <strong>is</strong> a log entry.
    /// </summary>
    Default = 1,
    /// <summary>
    /// This represents a compacted entry.
    /// </summary>
    Compacted = 2,
    /// <summary>
    /// This represents an entry that compaction is pending.
    /// </summary>
    PendingCompaction = 3
}
