using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Provider-neutral pooled cache for immutable recoverable stream records.
    /// </summary>
    /// <typeparam name="TQueueMessage">The source record type.</typeparam>
    public sealed class RecoverableStreamQueueCache<TQueueMessage> : IRecoverableStreamQueueCache<TQueueMessage>
    {
        private readonly int _defaultMaxAddCount;
        private readonly IObjectPool<FixedSizeBuffer> _bufferPool;
        private readonly IRecoverableStreamDataAdapter<TQueueMessage> _dataAdapter;
        private readonly IEvictionStrategy _evictionStrategy;
        private readonly IQueueFlowController? _flowController;
        private readonly int? _maxCacheSize;
        private readonly PooledQueueCache _cache;
        private FixedSizeBuffer? _currentBuffer;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecoverableStreamQueueCache{TQueueMessage}"/> class.
        /// </summary>
        public RecoverableStreamQueueCache(
            int defaultMaxAddCount,
            IObjectPool<FixedSizeBuffer> bufferPool,
            IRecoverableStreamDataAdapter<TQueueMessage> dataAdapter,
            IEvictionStrategy evictionStrategy,
            ILogger logger,
            IQueueFlowController? flowController = null,
            ICacheMonitor? cacheMonitor = null,
            TimeSpan? cacheMonitorWriteInterval = null,
            TimeSpan? metadataMinTimeInCache = null,
            int? maxCacheSize = null)
        {
            if (defaultMaxAddCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(defaultMaxAddCount));
            }

            _defaultMaxAddCount = defaultMaxAddCount;
            _bufferPool = bufferPool ?? throw new ArgumentNullException(nameof(bufferPool));
            _dataAdapter = dataAdapter ?? throw new ArgumentNullException(nameof(dataAdapter));
            _evictionStrategy = evictionStrategy ?? throw new ArgumentNullException(nameof(evictionStrategy));
            _flowController = flowController;
            if (maxCacheSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxCacheSize));
            }

            _maxCacheSize = maxCacheSize;
            _cache = new PooledQueueCache(
                dataAdapter,
                logger ?? throw new ArgumentNullException(nameof(logger)),
                cacheMonitor,
                cacheMonitorWriteInterval,
                metadataMinTimeInCache);
            _evictionStrategy.PurgeObservable = _cache;
            _evictionStrategy.OnPurged = OnPurged;
        }

        /// <summary>
        /// Gets the most recently purged provider offset.
        /// </summary>
        public string? LastPurgedOffset { get; private set; }

        /// <summary>
        /// Gets the number of records currently held in the cache.
        /// </summary>
        public int ItemCount => _cache.ItemCount;

        /// <summary>
        /// Tries to get the newest cached provider position.
        /// </summary>
        public bool TryGetNewestPosition(
            [NotNullWhen(true)] out StreamSequenceToken? token,
            [NotNullWhen(true)] out string? offset)
        {
            if (_cache.Newest is not { } newest)
            {
                token = null;
                offset = null;
                return false;
            }

            token = _dataAdapter.GetSequenceToken(ref newest);
            offset = _dataAdapter.GetOffset(ref newest);
            return true;
        }

        /// <summary>
        /// Packs and adds ordered source records to the cache.
        /// </summary>
        public IReadOnlyList<StreamPosition> Add(
            IReadOnlyList<TQueueMessage> messages,
            DateTime dequeueTimeUtc)
        {
            ArgumentNullException.ThrowIfNull(messages);
            var positions = new List<StreamPosition>(messages.Count);
            var cachedMessages = new List<CachedMessage>(messages.Count);
            var allocatedBuffers = new List<FixedSizeBuffer>();
            FixedSizeBuffer? batchBuffer = null;
            try
            {
                foreach (var message in messages)
                {
                    var position = _dataAdapter.GetStreamPosition(message);
                    cachedMessages.Add(_dataAdapter.FromQueueMessage(position, message, dequeueTimeUtc, GetBatchSegment));
                    positions.Add(position);
                }
            }
            catch
            {
                foreach (var buffer in allocatedBuffers)
                {
                    buffer.Dispose();
                }

                throw;
            }

            _cache.Add(cachedMessages, dequeueTimeUtc);
            foreach (var buffer in allocatedBuffers)
            {
                _evictionStrategy.OnBlockAllocated(buffer);
            }

            _currentBuffer = batchBuffer;
            return positions;

            ArraySegment<byte> GetBatchSegment(int size)
            {
                if (batchBuffer is not null && batchBuffer.TryGetSegment(size, out var segment))
                {
                    return segment;
                }

                var buffer = _bufferPool.Allocate();
                if (!buffer.TryGetSegment(size, out segment))
                {
                    buffer.Dispose();
                    buffer = new FixedSizeBuffer(size);
                    _ = buffer.TryGetSegment(size, out segment);
                }

                allocatedBuffers.Add(buffer);
                batchBuffer = buffer;
                return segment;
            }
        }

        /// <inheritdoc />
        public int GetMaxAddCount()
        {
            var result = _defaultMaxAddCount;
            if (_maxCacheSize is { } maxCacheSize)
            {
                result = Math.Min(result, Math.Max(0, maxCacheSize - _cache.ItemCount));
            }

            if (_flowController is not null)
            {
                result = Math.Min(result, _flowController.GetMaxAddCount());
            }

            return result;
        }

        /// <inheritdoc />
        public void AddToCache(IList<IBatchContainer> messages)
        {
            // Source records are admitted directly by the receiver coordinator.
        }

        /// <inheritdoc />
        public bool TryPurgeFromCache([MaybeNullWhen(false)] out IList<IBatchContainer> purgedItems)
        {
            purgedItems = null;
            // Pressure indicates that a lagging cursor can still need the oldest records. Time-based
            // eviction in that state would turn backpressure into data loss. UpdateDeliveryProgress
            // removes records through the safe delivery watermark and releases pressure as consumers advance.
            if (!IsUnderPressure())
            {
                _evictionStrategy.PerformPurge(DateTime.UtcNow);
                if (_cache.IsEmpty)
                {
                    _currentBuffer = null;
                }
            }

            return false;
        }

        /// <inheritdoc />
        public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token)
            => new Cursor(_cache, streamId, token);

        /// <inheritdoc />
        public bool IsUnderPressure() => GetMaxAddCount() <= 0;

        /// <inheritdoc />
        public void UpdateDeliveryProgress(StreamSequenceToken? earliestSubscriptionToken, DateTime utcNow)
        {
            if (earliestSubscriptionToken is null)
            {
                return;
            }

            CachedMessage? lastPurged = null;
            var itemsPurged = 0;
            while (_cache.Oldest is { } oldest
                && _dataAdapter.Compare(ref oldest, earliestSubscriptionToken) <= 0)
            {
                LastPurgedOffset = _dataAdapter.GetOffset(ref oldest);
                lastPurged = oldest;
                itemsPurged++;
                _cache.RemoveOldestMessage();
            }

            _evictionStrategy.OnPurgeCompleted(lastPurged, itemsPurged);
            if (_cache.IsEmpty)
            {
                _currentBuffer = null;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            CachedMessage? lastPurged = null;
            var itemsPurged = 0;
            while (_cache.Oldest is { } oldest)
            {
                lastPurged = oldest;
                itemsPurged++;
                _cache.RemoveOldestMessage();
            }

            _evictionStrategy.OnPurgeCompleted(lastPurged, itemsPurged);
            _currentBuffer = null;
            _evictionStrategy.OnPurged = null;
        }

        private void OnPurged(CachedMessage? lastPurged, CachedMessage? newest)
        {
            if (lastPurged is { } message)
            {
                LastPurgedOffset = _dataAdapter.GetOffset(ref message);
            }
        }

        private sealed class Cursor : IQueueCacheCursor, IQueueCacheCursorProgress
        {
            private readonly PooledQueueCache _cache;
            private readonly object _cursor;
            private IBatchContainer? _current;

            public Cursor(PooledQueueCache cache, StreamId streamId, StreamSequenceToken? token)
            {
                _cache = cache;
                _cursor = cache.GetCursor(streamId, token);
            }

            public void Dispose()
            {
            }

            public StreamSequenceToken? SafeSequenceToken => _cache.GetSafeSequenceToken(_cursor);

            public void AdvancePast(StreamSequenceToken token) => _cache.AdvanceCursorPast(_cursor, token);

            public IBatchContainer? GetCurrent(out Exception? exception)
            {
                exception = null;
                return _current;
            }

            public bool MoveNext()
            {
                if (!_cache.TryGetNextMessage(_cursor, out var next))
                {
                    return false;
                }

                _current = next;
                return true;
            }

            public void Refresh(StreamSequenceToken token) => _cache.Refresh(_cursor, token);

            public void RecordDeliveryFailure()
            {
            }

            public void RecordDeliverySuccess() => _cache.RecordDeliverySuccess(_cursor);
        }
    }
}
