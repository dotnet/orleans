namespace Orleans.Streaming.AdoNet;

internal static class AdoNetStreamReplayStatus
{
    public const string Acquired = nameof(Acquired);
    public const string Active = nameof(Active);
    public const string Released = nameof(Released);
    public const string OwnershipLost = nameof(OwnershipLost);
    public const string Expired = nameof(Expired);
    public const string HistoryUnavailable = nameof(HistoryUnavailable);
}

internal record AdoNetStreamReplayLeaseState(
    string Status,
    string? ServiceId,
    string? ProviderId,
    string? QueueId,
    string? ReaderId,
    long? OwnerEpoch,
    long? Watermark,
    DateTime? ExpiresOn,
    long? NextMessageId,
    long? Checkpoint,
    long? EarliestMessageId,
    long? TailMessageId);

internal record AdoNetStreamReplayPage(
    AdoNetStreamReplayLeaseState Lease,
    IReadOnlyList<AdoNetStreamMessage> Messages);

internal record AdoNetStreamReplayRow(
    AdoNetStreamReplayLeaseState Lease,
    AdoNetStreamMessage? Message);
