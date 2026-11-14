using System.Threading;

namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Assigns queue cache positions to dequeued messages and resolves positions to their message identifiers.
/// </summary>
/// <remarks>
/// Queue caches require sequence tokens which increase in dequeue order across reads.
/// Message identifiers do not provide this guarantee because a message released by a previous owner,
/// retried after a delivery failure, or committed late becomes visible after messages with greater identifiers.
/// Positions are unique within the provider, so a position never resolves to a message dequeued by another receiver.
/// </remarks>
internal sealed class AdoNetStreamPositions
{
    private readonly ConcurrentDictionary<long, long> _messageIds = new();
    private long _lastPosition;

    /// <summary>
    /// Assigns the next position to the specified message.
    /// </summary>
    public long Add(long messageId)
    {
        var position = Interlocked.Increment(ref _lastPosition);
        _messageIds[position] = messageId;
        return position;
    }

    /// <summary>
    /// Gets the message identifier for a position which has not yet been removed.
    /// </summary>
    public bool TryGetMessageId(long position, out long messageId) => _messageIds.TryGetValue(position, out messageId);

    /// <summary>
    /// Removes a position once its message has left the queue cache or is no longer held by the receiver.
    /// </summary>
    public void Remove(long position) => _messageIds.TryRemove(position, out _);
}
