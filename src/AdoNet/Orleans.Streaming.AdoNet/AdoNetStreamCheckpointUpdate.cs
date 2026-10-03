namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Describes the result of an epoch-fenced checkpoint update.
/// </summary>
internal record AdoNetStreamCheckpointUpdate(
    string ServiceId,
    string ProviderId,
    string QueueId,
    long OwnerEpoch,
    long? Checkpoint,
    bool Updated);
