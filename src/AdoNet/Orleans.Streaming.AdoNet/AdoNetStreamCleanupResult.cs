namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Describes one bounded retained-log cleanup operation.
/// </summary>
internal record AdoNetStreamCleanupResult(
    bool Ran,
    int DeletedCount,
    long? DeletedThroughMessageId,
    int HardDeletedCount,
    long? HardDeletedFromMessageId,
    long? HardDeletedThroughMessageId,
    long? Checkpoint,
    long? EarliestMessageId,
    long? TailMessageId);
