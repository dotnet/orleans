using System;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Azure.Messaging.EventHubs;

namespace Orleans.Streaming.EventHubs
{
    /// <summary>
    /// EventHub queue cache
    /// </summary>
    public partial class EventHubQueueCache : IEventHubQueueCache
    {
        /// <summary>
        /// Gets the Event Hub partition cached by this instance.
        /// </summary>
        public string Partition { get; private set; }

        /// <summary>
        /// Default max number of items that can be added to the cache between purge calls
        /// </summary>
        private readonly int defaultMaxAddCount;
        /// <summary>
        /// Underlying message cache implementation
        /// Protected for test purposes
        /// </summary>
        protected readonly PooledQueueCache cache;
        private readonly IObjectPool<FixedSizeBuffer> bufferPool;
        private readonly IEventHubDataAdapter dataAdapter;
        private readonly IEvictionStrategy evictionStrategy;
        private readonly IStreamQueueCheckpointer<string> checkpointer;
        private bool certifiedDeliveryProgress;
        private readonly ILogger logger;
        private readonly AggregatedCachePressureMonitor cachePressureMonitor;
        private readonly ICacheMonitor cacheMonitor;
        private FixedSizeBuffer? currentBuffer;
        private readonly List<FixedSizeBuffer> pendingBuffers = [];
        private int pendingBufferNotification;
        private List<EventData>? committedMessages;
        private List<StreamPosition>? committedPositions;
        private StreamSequenceToken? deliveryBoundary;

        /// <summary>
        /// EventHub queue cache.
        /// </summary>
        /// <param name="partition">Partition this instance is caching.</param>
        /// <param name="defaultMaxAddCount">
        /// Maximum read size. Certified processing also uses this value as the maximum number of owned raw-data pool buffers.
        /// </param>
        /// <param name="bufferPool">The raw data block pool.</param>
        /// <param name="dataAdapter">The adapter used to convert Event Hubs data into cached messages.</param>
        /// <param name="evictionStrategy">The strategy used to evict cached messages.</param>
        /// <param name="checkpointer">The checkpointer used to persist queue progress.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="cacheMonitor">The cache statistics monitor.</param>
        /// <param name="cacheMonitorWriteInterval">The interval between cache statistics updates.</param>
        /// <param name="metadataMinTimeInCache">The minimum time metadata remains in the cache.</param>
        public EventHubQueueCache(
            string partition,
            int defaultMaxAddCount,
            IObjectPool<FixedSizeBuffer> bufferPool,
            IEventHubDataAdapter dataAdapter,
            IEvictionStrategy evictionStrategy,
            IStreamQueueCheckpointer<string> checkpointer,
            ILogger logger,
            ICacheMonitor cacheMonitor,
            TimeSpan? cacheMonitorWriteInterval,
            TimeSpan? metadataMinTimeInCache)
        {
            this.Partition = partition;
            this.defaultMaxAddCount = defaultMaxAddCount;
            this.bufferPool = bufferPool;
            this.dataAdapter = dataAdapter;
            this.checkpointer = checkpointer;
            this.cache = new PooledQueueCache(dataAdapter, logger, cacheMonitor, cacheMonitorWriteInterval, metadataMinTimeInCache);
            this.cacheMonitor = cacheMonitor;
            this.evictionStrategy = evictionStrategy;
            this.evictionStrategy.OnPurged = this.OnPurge;
            this.evictionStrategy.PurgeObservable = new CertifiedPurgeView(this);
            this.cachePressureMonitor = new AggregatedCachePressureMonitor(logger, cacheMonitor);
            this.logger = logger;
        }

        /// <inheritdoc />
        public void SignalPurge()
        {
            try
            {
                this.evictionStrategy.PerformPurge(DateTime.UtcNow);
            }
            finally
            {
                if (this.cache.IsEmpty) this.currentBuffer = null;
            }
        }

        internal void UpdateDeliveryProgress(StreamSequenceToken safeToken, DateTime utcNow)
        {
            ArgumentNullException.ThrowIfNull(safeToken);
            deliveryBoundary = safeToken;
            try
            {
                evictionStrategy.PerformPurge(utcNow);
            }
            finally
            {
                if (cache.IsEmpty) currentBuffer = null;
                deliveryBoundary = null;
            }
        }

        // Custom compositions retain their released behavior. Only native components or
        // fault-injection tests which explicitly enabled this cache use certified progress.
        internal bool TryEnableCertifiedDeliveryProgress()
            => certifiedDeliveryProgress = certifiedDeliveryProgress
                || (GetType() == typeof(EventHubQueueCache)
                    && dataAdapter.GetType() == typeof(EventHubDataAdapter)
                    && evictionStrategy.GetType() == typeof(ChronologicalEvictionStrategy));

        internal void EnableCertifiedDeliveryProgress() => certifiedDeliveryProgress = true;

        private sealed class CertifiedPurgeView(EventHubQueueCache owner) : IPurgeObservable
        {
            public CachedMessage? Newest => owner.cache.Newest;
            public CachedMessage? Oldest => owner.cache.Oldest;
            public int ItemCount => owner.cache.ItemCount;
            public bool IsEmpty => owner.cache.IsEmpty;

            public bool TryRemoveOldestMessage()
            {
                if (!owner.certifiedDeliveryProgress)
                {
                    owner.cache.RemoveOldestMessage();
                    return true;
                }

                if (owner.deliveryBoundary is not { } boundary || Oldest is not { } oldest
                    || owner.dataAdapter.Compare(ref oldest, boundary) > 0)
                {
                    return false;
                }

                owner.cache.RemoveOldestMessage();
                return true;
            }

            public void RemoveOldestMessage()
            {
                if (!TryRemoveOldestMessage())
                {
                    throw new InvalidOperationException("Eviction attempted to pass the certified delivery prefix.");
                }
            }
        }

        /// <summary>
        /// Add cache pressure monitor to the cache's back pressure algorithm
        /// </summary>
        /// <param name="monitor">The cache pressure monitor.</param>
        public void AddCachePressureMonitor(ICachePressureMonitor monitor)
        {
            monitor.CacheMonitor = this.cacheMonitor;
            this.cachePressureMonitor.AddCachePressureMonitor(monitor);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        /// <filterpriority>2</filterpriority>
        public void Dispose()
        {
            this.evictionStrategy.OnPurged = null;
        }

        /// <summary>
        /// The limit of the maximum number of items that can be added
        /// </summary>
        /// <returns>The maximum number of items which can currently be added.</returns>
        /// <remarks>
        /// Certified processing reserves one possible new pool buffer per record and resumes admission as completed buffers are reclaimed.
        /// </remarks>
        public int GetMaxAddCount()
        {
            if (cachePressureMonitor.IsUnderPressure(DateTime.UtcNow))
            {
                return 0;
            }

            // A native record uses at most one new pool buffer. Reserve that worst case for
            // each read, bounding certified retention by the buffers one full read can allocate.
            // Unlike averaged pressure, this limit cannot be diluted by healthy consumers.
            return certifiedDeliveryProgress
                ? Math.Max(0, defaultMaxAddCount - ((ChronologicalEvictionStrategy)evictionStrategy).BufferCount
                    - (pendingBuffers.Count - pendingBufferNotification))
                : defaultMaxAddCount;
        }

        /// <summary>
        /// Add a list of EventHub EventData to the cache.
        /// </summary>
        /// <param name="messages">The Event Hub messages to cache.</param>
        /// <param name="dequeueTimeUtc">The UTC time when the messages were dequeued.</param>
        /// <returns>The stream positions of the cached messages.</returns>
        public List<StreamPosition> Add(List<EventData> messages, DateTime dequeueTimeUtc)
        {
            if (!certifiedDeliveryProgress)
            {
                var legacyPositions = new List<StreamPosition>(messages.Count);
                var legacyMessages = new List<CachedMessage>(messages.Count);
                foreach (var message in messages)
                {
                    var position = dataAdapter.GetStreamPosition(Partition, message);
                    legacyMessages.Add(dataAdapter.FromQueueMessage(position, message, dequeueTimeUtc, GetSegment));
                    legacyPositions.Add(position);
                }
                cache.Add(legacyMessages, dequeueTimeUtc);
                return legacyPositions;
            }

            if (committedMessages is not null && !ReferenceEquals(committedMessages, messages))
            {
                throw new InvalidOperationException("The previously admitted Event Hubs batch must finish its handoff before another batch is admitted.");
            }

            if (committedMessages is null)
            {
                var originalBuffer = currentBuffer;
                var originalPosition = originalBuffer?.Position ?? 0;
                var positions = new List<StreamPosition>(messages.Count);
                var cachedMessages = new List<CachedMessage>(messages.Count);
                try
                {
                    foreach (var message in messages)
                    {
                        var position = dataAdapter.GetStreamPosition(Partition, message);
                        cachedMessages.Add(dataAdapter.FromQueueMessage(position, message, dequeueTimeUtc, GetSegment));
                        positions.Add(position);
                    }

                    cache.Add(cachedMessages, dequeueTimeUtc);
                }
                catch
                {
                    originalBuffer?.ResetTo(originalPosition);
                    currentBuffer = originalBuffer;
                    foreach (var buffer in pendingBuffers) buffer.Dispose();
                    pendingBuffers.Clear();
                    throw;
                }

                committedMessages = messages;
                committedPositions = positions;
            }

            // Metadata admission is complete. Retry notification without admitting the records twice.
            while (pendingBufferNotification < pendingBuffers.Count)
            {
                evictionStrategy.OnBlockAllocated(pendingBuffers[pendingBufferNotification]);
                pendingBufferNotification++;
            }

            var result = committedPositions!;
            pendingBuffers.Clear();
            pendingBufferNotification = 0;
            committedMessages = null;
            committedPositions = null;
            return result;
        }

        /// <summary>
        /// Get a cursor into the cache to read events from a stream.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="sequenceToken">The position from which to begin reading.</param>
        /// <returns>A cache cursor.</returns>
        [Obsolete("Use TryGetCursor instead.")]
        public object GetCursor(StreamId streamId, StreamSequenceToken? sequenceToken)
        {
            var result = cache.TryGetCursor(streamId, sequenceToken);
            return result.Kind switch
            {
                QueueCacheCursorResultKind.Success => result.Cursor!,
                QueueCacheCursorResultKind.CacheMiss => throw result.CacheMiss!.Value.ToException(),
                _ => throw new InvalidOperationException($"Unexpected cursor result: {result.Kind}."),
            };
        }

        /// <inheritdoc />
        public QueueCacheCursorResult<object> TryGetCursor(StreamId streamId, StreamSequenceToken? sequenceToken)
            => cache.TryGetCursor(streamId, sequenceToken);

        /// <inheritdoc />
        public QueueCacheCursorResult<object> TryGetCursorAtPosition(
            StreamId streamId,
            StreamSubscriptionStartPosition startPosition)
            => cache.TryGetCursorAtPosition(streamId, startPosition);

        /// <inheritdoc />
        public void Refresh(object cursor, StreamSequenceToken? sequenceToken)
        {
            cache.Refresh(cursor, sequenceToken);
        }

        /// <summary>
        /// Try to get the next message in the cache for the provided cursor.
        /// </summary>
        /// <param name="cursorObj">The cache cursor.</param>
        /// <param name="message">The next message when one is available.</param>
        /// <returns><see langword="true"/> when a message was returned; otherwise, <see langword="false"/>.</returns>
        [Obsolete("Use TryGetNextMessageWithResult instead.")]
        public bool TryGetNextMessage(object cursorObj, [NotNullWhen(true)] out IBatchContainer? message)
        {
            var result = TryGetNextMessageWithResult(cursorObj, out message);
            if (result.CacheMiss is { } cacheMiss)
            {
                throw cacheMiss.ToException();
            }

            return result.Kind switch
            {
                QueueCacheCursorMoveResultKind.Success => true,
                QueueCacheCursorMoveResultKind.NoData => false,
                _ => throw new InvalidOperationException("The cursor move result is not initialized."),
            };
        }

        /// <inheritdoc />
        public QueueCacheCursorMoveResult TryGetNextMessageWithResult(object cursorObj, out IBatchContainer? message)
        {
            var result = cache.TryGetNextMessageWithResult(cursorObj, out message);
            if (result.Kind == QueueCacheCursorMoveResultKind.Success)
            {
                RecordCachePressure(message!);
            }

            return result;
        }

        private void RecordCachePressure(IBatchContainer message)
        {
            double cachePressureContribution;
            cachePressureMonitor.RecordCachePressureContribution(
                TryCalculateCachePressureContribution(message.SequenceToken, out cachePressureContribution)
                    ? cachePressureContribution
                    : 0.0);
        }

        /// <summary>
        /// Handles cache purge signals
        /// </summary>
        /// <param name="lastItemPurged"></param>
        /// <param name="newestItem"></param>
        private void OnPurge(CachedMessage? lastItemPurged, CachedMessage? newestItem)
        {
            if (lastItemPurged.HasValue && newestItem.HasValue)
            {
                LogDebugCachePeriod(
                    new(lastItemPurged.Value.EnqueueTimeUtc),
                    new(newestItem.Value.EnqueueTimeUtc),
                    new(lastItemPurged.Value.DequeueTimeUtc),
                    new(newestItem.Value.DequeueTimeUtc));
            }
            if (!certifiedDeliveryProgress && lastItemPurged is { } purged)
            {
                checkpointer.Update(dataAdapter.GetOffset(purged), DateTime.UtcNow, CancellationToken.None);
            }
        }

        /// <summary>
        /// cachePressureContribution should be a double between 0-1, indicating how much danger the item is of being removed from the cache.
        ///   0 indicating  no danger,
        ///   1 indicating removal is imminent.
        /// </summary>
        private bool TryCalculateCachePressureContribution(StreamSequenceToken token, out double cachePressureContribution)
        {
            cachePressureContribution = 0;
            // if cache is empty or has few items, don't calculate pressure
            if (cache.IsEmpty ||
                !cache.Newest.HasValue ||
                !cache.Oldest.HasValue ||
                cache.Newest.Value.SequenceNumber - cache.Oldest.Value.SequenceNumber < 10 * defaultMaxAddCount) // not enough items in cache.
            {
                return false;
            }

            IEventHubPartitionLocation location = (IEventHubPartitionLocation)token;
            double cacheSize = cache.Newest.Value.SequenceNumber - cache.Oldest.Value.SequenceNumber;
            long distanceFromNewestMessage = cache.Newest.Value.SequenceNumber - location.SequenceNumber;
            // pressure is the ratio of the distance from the front of the cache to the
            cachePressureContribution = distanceFromNewestMessage / cacheSize;

            return true;
        }

        private ArraySegment<byte> GetSegment(int size)
        {
            // get segment from current block
            ArraySegment<byte> segment;
            if (currentBuffer == null || !currentBuffer.TryGetSegment(size, out segment))
            {
                // no block or block full, get new block and try again
                var newBuffer = bufferPool.Allocate();
                // if this fails with a clean block, then requested size is too big; return the
                // unused block to the pool and fail. Registering it with the eviction strategy
                // before confirming the segment fits would leak it, because a batch that never
                // commits is never reclaimed by the purge-time logic.
                if (!newBuffer.TryGetSegment(size, out segment))
                {
                    newBuffer.Dispose();
                    throw new ArgumentOutOfRangeException(nameof(size), $"Message size is too big. MessageSize: {size}");
                }
                currentBuffer = newBuffer;
                if (certifiedDeliveryProgress)
                {
                    pendingBuffers.Add(currentBuffer);
                }
                else
                {
                    evictionStrategy.OnBlockAllocated(currentBuffer);
                }
            }
            return segment;
        }

        private readonly struct DateTimeLogRecord(DateTime ts)
        {
            public override string ToString() => LogFormatter.PrintDate(ts);
        }

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "CachePeriod: EnqueueTimeUtc: {OldestEnqueueTimeUtc} to {NewestEnqueueTimeUtc}, DequeueTimeUtc: {OldestDequeueTimeUtc} to {NewestDequeueTimeUtc}"
        )]
        private partial void LogDebugCachePeriod(
            DateTimeLogRecord oldestEnqueueTimeUtc,
            DateTimeLogRecord newestEnqueueTimeUtc,
            DateTimeLogRecord oldestDequeueTimeUtc,
            DateTimeLogRecord newestDequeueTimeUtc);
    }
}
