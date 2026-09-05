using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common;

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
    private readonly long _maxCacheSizeBytes;
    private readonly Queue<long> _messageSizes = new();
    private readonly PooledQueueCache _cache;
    private FixedSizeBuffer? _currentBuffer;
    private long? _pendingMessageSize;

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
        : this(
            defaultMaxAddCount,
            bufferPool,
            dataAdapter,
            evictionStrategy,
            logger,
            long.MaxValue,
            flowController,
            cacheMonitor,
            cacheMonitorWriteInterval,
            metadataMinTimeInCache,
            maxCacheSize)
    {
    }

    /// <summary>
    /// Initializes a cache with a per-partition encoded-data budget.
    /// </summary>
    /// <param name="defaultMaxAddCount">The maximum number of records requested per read.</param>
    /// <param name="bufferPool">The pool which provides encoded-data buffers.</param>
    /// <param name="dataAdapter">The source record adapter.</param>
    /// <param name="evictionStrategy">The cache eviction strategy.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="maxCacheSizeBytes">The positive budget for encoded segments belonging to retained records.</param>
    /// <param name="flowController">An optional additional flow controller.</param>
    /// <param name="cacheMonitor">An optional cache monitor.</param>
    /// <param name="cacheMonitorWriteInterval">The cache monitoring interval.</param>
    /// <param name="metadataMinTimeInCache">The minimum metadata retention interval.</param>
    /// <param name="maxCacheSize">An optional maximum retained record count.</param>
    /// <remarks>
    /// Records are admitted in order. An empty cache admits one record larger than the byte budget
    /// so that delivery can make progress. Capacity planning also accounts for buffer slack and pool
    /// retention, a fetched source batch, metadata, and deserialized delivery objects.
    /// </remarks>
    public RecoverableStreamQueueCache(
        int defaultMaxAddCount,
        IObjectPool<FixedSizeBuffer> bufferPool,
        IRecoverableStreamDataAdapter<TQueueMessage> dataAdapter,
        IEvictionStrategy evictionStrategy,
        ILogger logger,
        long maxCacheSizeBytes,
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

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCacheSizeBytes);
        _maxCacheSizeBytes = maxCacheSizeBytes;
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
    /// Gets the encoded segment bytes belonging to records currently held in the cache.
    /// </summary>
    public long SizeInBytes { get; private set; }

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
    /// Packs and adds the ordered prefix of source records which fits the cache's capacity.
    /// </summary>
    /// <remarks>
    /// The returned positions identify the admitted prefix. A packing exception rolls back the entire
    /// attempted prefix. An empty cache admits one oversized record to permit delivery progress.
    /// </remarks>
    public IReadOnlyList<StreamPosition> Add(
        IReadOnlyList<TQueueMessage> messages,
        DateTime dequeueTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (IsUnderBytePressure())
        {
            return [];
        }

        var count = _maxCacheSize is { } maxCacheSize
            ? Math.Min(messages.Count, maxCacheSize - _cache.ItemCount)
            : messages.Count;
        var positions = new List<StreamPosition>(count);
        var cachedMessages = new List<CachedMessage>(count);
        var messageSizes = new List<long>(count);
        var allocatedBuffers = new List<FixedSizeBuffer>();
        var initialBuffer = _currentBuffer;
        var initialBufferPosition = initialBuffer?.Position ?? 0;
        var batchBuffer = initialBuffer;
        var batchSize = 0L;
        var recordSize = 0L;
        long? pendingMessageSize = null;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var recordBuffer = batchBuffer;
                var recordBufferPosition = recordBuffer?.Position ?? 0;
                var allocatedBufferCount = allocatedBuffers.Count;
                recordSize = 0;
                try
                {
                    var message = messages[i];
                    var position = _dataAdapter.GetStreamPosition(message);
                    cachedMessages.Add(_dataAdapter.FromQueueMessage(position, message, dequeueTimeUtc, GetBatchSegment));
                    positions.Add(position);
                    messageSizes.Add(recordSize);
                    batchSize += recordSize;
                }
                catch (ByteBudgetExceededException exception)
                {
                    recordBuffer?.ResetTo(recordBufferPosition);
                    for (var j = allocatedBuffers.Count - 1; j >= allocatedBufferCount; j--)
                    {
                        allocatedBuffers[j].Dispose();
                        allocatedBuffers.RemoveAt(j);
                    }

                    batchBuffer = recordBuffer;
                    pendingMessageSize = exception.RequiredSize;
                    break;
                }
            }
        }
        catch
        {
            initialBuffer?.ResetTo(initialBufferPosition);
            foreach (var buffer in allocatedBuffers)
            {
                buffer.Dispose();
            }

            throw;
        }

        _cache.Add(cachedMessages, dequeueTimeUtc);
        foreach (var size in messageSizes)
        {
            _messageSizes.Enqueue(size);
        }

        SizeInBytes += batchSize;
        _pendingMessageSize = pendingMessageSize;
        foreach (var buffer in allocatedBuffers)
        {
            _evictionStrategy.OnBlockAllocated(buffer);
        }

        _currentBuffer = batchBuffer;
        return positions;

        ArraySegment<byte> GetBatchSegment(int size)
        {
            var requiredSize = checked(recordSize + size);
            if ((_cache.ItemCount > 0 || cachedMessages.Count > 0)
                && requiredSize > _maxCacheSizeBytes - SizeInBytes - batchSize)
            {
                throw new ByteBudgetExceededException(requiredSize);
            }

            recordSize = requiredSize;
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
        if (IsUnderBytePressure())
        {
            return 0;
        }

        var result = _defaultMaxAddCount;
        if (_maxCacheSize is { } maxCacheSize)
        {
            result = Math.Min(result, Math.Max(0, maxCacheSize - _cache.ItemCount));
        }

        if (_flowController is not null)
        {
            var flowControlLimit = _flowController.GetMaxAddCount();
            if (flowControlLimit >= 0)
            {
                result = Math.Min(result, flowControlLimit);
            }
        }

        return result;
    }

    private bool IsUnderBytePressure()
        => _cache.ItemCount > 0
            && (SizeInBytes >= _maxCacheSizeBytes
                || _pendingMessageSize > _maxCacheSizeBytes - SizeInBytes);

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
            UpdateByteAccounting();
        }

        return false;
    }

    /// <inheritdoc />
    public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token)
        => new Cursor(_cache, streamId, token);

    /// <inheritdoc />
    public IQueueCacheCursor GetCacheCursorAtPosition(
        StreamId streamId,
        StreamSubscriptionStartPosition startPosition)
        => new Cursor(_cache, streamId, startPosition);

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

        UpdateByteAccounting();
        _evictionStrategy.OnPurgeCompleted(lastPurged, itemsPurged);
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

        UpdateByteAccounting();
        _pendingMessageSize = null;
        _evictionStrategy.OnPurgeCompleted(lastPurged, itemsPurged);
        _evictionStrategy.OnPurged = null;
    }

    private void OnPurged(CachedMessage? lastPurged, CachedMessage? newest)
    {
        UpdateByteAccounting();
        if (lastPurged is { } message)
        {
            LastPurgedOffset = _dataAdapter.GetOffset(ref message);
        }
    }

    private void UpdateByteAccounting()
    {
        while (_messageSizes.Count > _cache.ItemCount)
        {
            SizeInBytes -= _messageSizes.Dequeue();
        }

        if (_cache.IsEmpty)
        {
            _currentBuffer = null;
        }
    }

    private sealed class ByteBudgetExceededException(long requiredSize) : Exception
    {
        public long RequiredSize { get; } = requiredSize;
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

        public Cursor(
            PooledQueueCache cache,
            StreamId streamId,
            StreamSubscriptionStartPosition startPosition)
        {
            _cache = cache;
            _cursor = cache.GetCursorAtPosition(streamId, startPosition);
        }

        public void Dispose()
        {
        }

        public StreamSequenceToken? SafeSequenceToken => _cache.GetSafeSequenceToken(_cursor);

        public void SetDeliveredThrough(StreamSequenceToken token)
            => _cache.SetCursorDeliveredThrough(_cursor, token);

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
            _cache.RecordDeliveryFailure(_cursor);
        }

        public void RecordDeliverySuccess() => _cache.RecordDeliverySuccess(_cursor);
    }
}
