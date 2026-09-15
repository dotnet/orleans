namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Describes the durable position and retained bounds of a stream partition.
/// </summary>
internal record AdoNetStreamPartitionState(
    string ServiceId,
    string ProviderId,
    string QueueId,
    long OwnerEpoch,
    long NextMessageId,
    long? Checkpoint,
    long? EarliestMessageId,
    long? TailMessageId);
