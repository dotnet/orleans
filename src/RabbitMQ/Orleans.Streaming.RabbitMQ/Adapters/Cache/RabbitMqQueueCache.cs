using Orleans.Runtime;
using Orleans.Streaming.RabbitMQ.Adapters;
using Orleans.Streaming.RabbitMQ.RabbitMQ;
using Orleans.Streams;

namespace Orleans.Streaming.RabbitMQ.Adapters.Cache;

internal sealed class RabbitMqQueueCache : IQueueCache
{
    private readonly object _lock = new();
    private readonly RabbitMQQueueCacheOptions _cacheOptions;
    private readonly LinkedList<CacheEntry> _entries = new();
    private readonly Dictionary<StreamId, List<CacheEntry>> _streamEntries = new();
    private readonly Dictionary<StreamId, HashSet<RabbitMqQueueCacheCursor>> _activeCursors = new();
    private readonly Dictionary<StreamId, LinkedListNode<PurgedHighWatermark>> _purgedHighWatermarks = new();
    private readonly LinkedList<PurgedHighWatermark> _purgedHighWatermarkOrder = new();
    private int _count;

    public RabbitMqQueueCache(RabbitMQQueueCacheOptions cacheOptions)
    {
        _cacheOptions = cacheOptions;
    }

    public int GetMaxAddCount() => _cacheOptions.CacheSize;

    public void AddToCache(IList<IBatchContainer> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        lock (_lock)
        {
            foreach (var message in messages.Cast<RabbitMqBatchContainer>())
            {
                var entry = new CacheEntry(message);
                _entries.AddLast(entry);

                if (!_streamEntries.TryGetValue(message.StreamId, out var streamEntries))
                {
                    _streamEntries.Add(message.StreamId, streamEntries = new());
                }

                streamEntries.Add(entry);
                if (_activeCursors.TryGetValue(message.StreamId, out var cursors))
                {
                    foreach (var cursor in cursors)
                    {
                        if (cursor.ShouldTrack(entry))
                        {
                            entry.PendingCursors.Add(cursor);
                        }
                    }
                }

                _count++;
            }
        }
    }

    public bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems)
    {
        lock (_lock)
        {
            List<IBatchContainer> purged = null;
            while (_entries.First is { Value.PendingCursors.Count: 0 } node)
            {
                var entry = node.Value;
                _entries.RemoveFirst();
                var streamEntries = _streamEntries[entry.Batch.StreamId];
                streamEntries.Remove(entry);
                if (streamEntries.Count == 0)
                {
                    _streamEntries.Remove(entry.Batch.StreamId);
                }

                RecordPurgedHighWatermark(entry.Batch.StreamId, entry.Batch.SequenceToken);
                (purged ??= new()).Add(entry.Batch);
                _count--;
            }

            purgedItems = purged;
            return purged is { Count: > 0 };
        }
    }

    public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken token)
    {
        lock (_lock)
        {
            _streamEntries.TryGetValue(streamId, out var streamEntries);
            if (token is not null)
            {
                if (streamEntries is { Count: > 0 })
                {
                    var low = streamEntries[0].Batch.SequenceToken;
                    if (token.Older(low))
                    {
                        throw new QueueCacheMissException(token, low, streamEntries[^1].Batch.SequenceToken);
                    }
                }
                else if (_purgedHighWatermarks.TryGetValue(streamId, out var purgedHigh) &&
                    !token.Newer(purgedHigh.Value.Token))
                {
                    throw new QueueCacheMissException(token, purgedHigh.Value.Token, purgedHigh.Value.Token);
                }
            }

            var cursor = new RabbitMqQueueCacheCursor(this, streamId, token);
            if (!_activeCursors.TryGetValue(streamId, out var cursors))
            {
                _activeCursors.Add(streamId, cursors = new());
            }

            cursors.Add(cursor);
            if (streamEntries is not null)
            {
                foreach (var entry in streamEntries)
                {
                    if (cursor.ShouldTrack(entry))
                    {
                        entry.PendingCursors.Add(cursor);
                    }
                }
            }

            return cursor;
        }
    }

    public bool IsUnderPressure()
    {
        lock (_lock)
        {
            return _count >= GetMaxAddCount();
        }
    }

    internal int PurgedHighWatermarkCount
    {
        get
        {
            lock (_lock)
            {
                return _purgedHighWatermarks.Count;
            }
        }
    }

    private void RecordPurgedHighWatermark(StreamId streamId, StreamSequenceToken token)
    {
        if (_purgedHighWatermarks.TryGetValue(streamId, out var existing))
        {
            existing.Value = new PurgedHighWatermark(streamId, token);
            _purgedHighWatermarkOrder.Remove(existing);
            _purgedHighWatermarkOrder.AddLast(existing);
        }
        else
        {
            _purgedHighWatermarks.Add(
                streamId,
                _purgedHighWatermarkOrder.AddLast(new PurgedHighWatermark(streamId, token)));
        }

        var capacity = Math.Max(1, _cacheOptions.CacheSize);
        while (_purgedHighWatermarks.Count > capacity)
        {
            var oldest = _purgedHighWatermarkOrder.First;
            _purgedHighWatermarkOrder.RemoveFirst();
            _purgedHighWatermarks.Remove(oldest.Value.StreamId);
        }
    }

    internal RabbitMqBatchContainer MoveNext(RabbitMqQueueCacheCursor cursor)
    {
        lock (_lock)
        {
            if (cursor.TryResetDeliveryFailure())
            {
                return cursor.Current;
            }

            if (cursor.CurrentEntry is { } current)
            {
                current.PendingCursors.Remove(cursor);
                cursor.MarkDelivered(current);
            }

            if (!_streamEntries.TryGetValue(cursor.StreamId, out var entries))
            {
                cursor.ClearCurrent();
                return null;
            }

            CacheEntry next = null;
            foreach (var entry in entries)
            {
                if (cursor.IsAfterLastDelivered(entry))
                {
                    next = entry;
                    break;
                }

                entry.PendingCursors.Remove(cursor);
            }

            if (next is null)
            {
                cursor.ClearCurrent();
                return null;
            }

            cursor.SetCurrent(next);
            return next.Batch;
        }
    }

    internal void DisposeCursor(RabbitMqQueueCacheCursor cursor)
    {
        lock (_lock)
        {
            if (_activeCursors.TryGetValue(cursor.StreamId, out var cursors))
            {
                cursors.Remove(cursor);
                if (cursors.Count == 0)
                {
                    _activeCursors.Remove(cursor.StreamId);
                }
            }

            foreach (var entry in _entries)
            {
                entry.PendingCursors.Remove(cursor);
            }

            cursor.ClearCurrent();
        }
    }

    internal sealed class CacheEntry
    {
        public CacheEntry(RabbitMqBatchContainer batch)
        {
            Batch = batch;
        }

        public RabbitMqBatchContainer Batch { get; }
        public HashSet<RabbitMqQueueCacheCursor> PendingCursors { get; } = new();
    }

    private readonly record struct PurgedHighWatermark(StreamId StreamId, StreamSequenceToken Token);
}
