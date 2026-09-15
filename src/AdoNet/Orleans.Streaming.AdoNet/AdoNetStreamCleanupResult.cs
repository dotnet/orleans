namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Describes one bounded stream partition retention cleanup operation.
/// </summary>
internal record AdoNetStreamCleanupResult(
    long? OwnerEpoch,
    bool Ran,
    int DeletedCount,
    long? DeletedThroughMessageId,
    int HardDeletedCount,
    long? HardDeletedFromMessageId,
    long? HardDeletedThroughMessageId,
    long? Checkpoint,
    long? ActiveReplayWatermark,
    long? EarliestMessageId,
    long? TailMessageId);
