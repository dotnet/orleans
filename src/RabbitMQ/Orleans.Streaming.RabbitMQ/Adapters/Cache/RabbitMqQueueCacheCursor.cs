using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Streaming.RabbitMQ.Adapters.Cache;

internal sealed class RabbitMqQueueCacheCursor : IQueueCacheCursor
{
    private readonly RabbitMqQueueCache _cache;
    private readonly StreamSequenceToken _requestedToken;
    private bool _disposed;
    private bool _deliveryFailed;
    private StreamSequenceToken _lastDelivered;

    public RabbitMqQueueCacheCursor(RabbitMqQueueCache cache, StreamId streamId, StreamSequenceToken requestedToken)
    {
        _cache = cache;
        StreamId = streamId;
        _requestedToken = requestedToken;
    }

    internal StreamId StreamId { get; }
    internal RabbitMqQueueCache.CacheEntry CurrentEntry { get; private set; }
    internal RabbitMqBatchContainer Current => CurrentEntry?.Batch;

    public IBatchContainer GetCurrent(out Exception exception)
    {
        exception = null;
        return Current;
    }

    public bool MoveNext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _cache.MoveNext(this) is not null;
    }

    public void Refresh(StreamSequenceToken token)
    {
    }

    public void RecordDeliveryFailure()
    {
        if (Current is not null)
        {
            _deliveryFailed = true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cache.DisposeCursor(this);
    }

    internal bool ShouldTrack(RabbitMqQueueCache.CacheEntry entry) =>
        IsAfterLastDelivered(entry);

    internal bool IsAfterLastDelivered(RabbitMqQueueCache.CacheEntry entry)
    {
        var token = entry.Batch.SequenceToken;
        if (_lastDelivered is not null && !token.Newer(_lastDelivered))
        {
            return false;
        }

        return _requestedToken is null || !token.Older(_requestedToken);
    }

    internal void SetCurrent(RabbitMqQueueCache.CacheEntry entry) => CurrentEntry = entry;

    internal bool TryResetDeliveryFailure()
    {
        if (!_deliveryFailed)
        {
            return false;
        }

        _deliveryFailed = false;
        return true;
    }

    internal void MarkDelivered(RabbitMqQueueCache.CacheEntry entry)
    {
        _lastDelivered = entry.Batch.SequenceToken;
        CurrentEntry = null;
    }

    internal void ClearCurrent() => CurrentEntry = null;
}
