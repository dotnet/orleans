namespace Orleans.Streams;

/// <summary>
/// Exposes contiguous partition progress for a queue cache cursor.
/// </summary>
public interface IQueueCacheCursorProgress
{
    /// <summary>
    /// Gets the latest contiguous partition position which is safe to reclaim.
    /// </summary>
    StreamSequenceToken? SafeSequenceToken { get; }

    /// <summary>
    /// Marks matching records through a previously acknowledged delivery token as delivered.
    /// </summary>
    /// <param name="token">The acknowledged delivery token.</param>
    void SetDeliveredThrough(StreamSequenceToken token);

    /// <summary>
    /// Records successful delivery or filtering of the current record.
    /// </summary>
    void RecordDeliverySuccess();
}

internal interface IQueueCacheCursorReplayState
{
    bool IsReplaying { get; }

    bool HasPendingLiveHandoff { get; }
}
