namespace Orleans.Journaling.Cosmos;

internal enum LogEntryType
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
    CompactionPending = 3
}
