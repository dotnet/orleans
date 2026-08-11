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
        private const int InitialMessageBlockSize = 256;
        private const int MaxMessageBlockSize = 16 * 1024;
        private const int MaxRetainedMessageBlocks = 1;

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
        private readonly ILogger logger;
        private readonly AggregatedCachePressureMonitor cachePressureMonitor;
        private readonly ICacheMonitor? cacheMonitor;
        private FixedSizeBuffer? currentBuffer;
        private readonly EventHubCacheMemoryController? memoryController;
        private int preferredBufferSize = EventHubCacheBufferPool.MinBufferSize;

        internal bool IsUnderMemoryPressure => memoryController?.IsUnderPressure == true;

        /// <summary>
        /// EventHub queue cache.
        /// </summary>
        /// <param name="partition">Partition this instance is caching.</param>
        /// <param name="defaultMaxAddCount">Default max number of items that can be added to the cache between purge calls.</param>
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
            ICacheMonitor? cacheMonitor,
            TimeSpan? cacheMonitorWriteInterval,
            TimeSpan? metadataMinTimeInCache)
            : this(
                partition,
                defaultMaxAddCount,
                bufferPool,
                dataAdapter,
                evictionStrategy,
                checkpointer,
                logger,
                cacheMonitor,
                cacheMonitorWriteInterval,
                metadataMinTimeInCache,
                null)
        {
        }

        internal EventHubQueueCache(
            string partition,
            int defaultMaxAddCount,
            IObjectPool<FixedSizeBuffer> bufferPool,
            IEventHubDataAdapter dataAdapter,
            IEvictionStrategy evictionStrategy,
            IStreamQueueCheckpointer<string> checkpointer,
            ILogger logger,
            ICacheMonitor? cacheMonitor,
            TimeSpan? cacheMonitorWriteInterval,
            TimeSpan? metadataMinTimeInCache,
            EventHubCacheMemoryController? memoryController)
        {
            this.Partition = partition;
            this.defaultMaxAddCount = defaultMaxAddCount;
            this.bufferPool = bufferPool;
            this.dataAdapter = dataAdapter;
            this.checkpointer = checkpointer;
            this.memoryController = memoryController;
            this.cache = memoryController is null
                ? new PooledQueueCache(dataAdapter, logger, cacheMonitor, cacheMonitorWriteInterval, metadataMinTimeInCache)
                : new PooledQueueCache(
                    dataAdapter,
                    logger,
                    cacheMonitor,
                    cacheMonitorWriteInterval,
                    metadataMinTimeInCache,
                    InitialMessageBlockSize,
                    MaxMessageBlockSize,
                    MaxRetainedMessageBlocks);
            this.cacheMonitor = cacheMonitor;
            this.evictionStrategy = evictionStrategy;
            this.evictionStrategy.OnPurged = this.OnPurge;
            this.evictionStrategy.PurgeObservable = this.cache;
            this.cachePressureMonitor = new AggregatedCachePressureMonitor(logger, cacheMonitor);
            this.logger = logger;
        }

        /// <inheritdoc />
        public void SignalPurge()
        {
            if (this.cachePressureMonitor.IsUnderPressure(DateTime.UtcNow))
            {
                return;
            }

            var previousMetadataSize = cache.AllocatedSizeInBytes;
            if (memoryController?.IsUnderPressure == true
                && evictionStrategy is IMemoryPressureEvictionStrategy memoryPressureEvictionStrategy)
            {
                memoryPressureEvictionStrategy.PerformMemoryPressurePurge(DateTime.UtcNow);
            }
            else
            {
                this.evictionStrategy.PerformPurge(DateTime.UtcNow);
            }

            UpdateMetadataMemory(previousMetadataSize);
            if (memoryController is not null
                && cache.IsEmpty
                && this.evictionStrategy is ChronologicalEvictionStrategy chronologicalEvictionStrategy)
            {
                chronologicalEvictionStrategy.ReleaseAllBuffers();
                currentBuffer = null;
                preferredBufferSize = EventHubCacheBufferPool.MinBufferSize;
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
            currentBuffer = null;
            if (memoryController is not null)
            {
                memoryController.AdjustActiveMetadataMemory(-cache.AllocatedSizeInBytes);
            }

            cache.Dispose();
            if (this.evictionStrategy is IDisposable disposableEvictionStrategy)
            {
                disposableEvictionStrategy.Dispose();
            }
        }

        /// <summary>
        /// The limit of the maximum number of items that can be added
        /// </summary>
        /// <returns>The maximum number of items which can currently be added.</returns>
        public int GetMaxAddCount()
        {
            if (cachePressureMonitor.IsUnderPressure(DateTime.UtcNow))
            {
                return 0;
            }

            if (memoryController?.IsUnderPressure == true)
            {
                SignalPurge();
            }

            return memoryController?.IsUnderPressure == true ? 0 : defaultMaxAddCount;
        }

        /// <summary>
        /// Add a list of EventHub EventData to the cache.
        /// </summary>
        /// <param name="messages">The Event Hub messages to cache.</param>
        /// <param name="dequeueTimeUtc">The UTC time when the messages were dequeued.</param>
        /// <returns>The stream positions of the cached messages.</returns>
        public List<StreamPosition> Add(List<EventData> messages, DateTime dequeueTimeUtc)
        {
            List<StreamPosition> positions = new List<StreamPosition>();
            List<CachedMessage> cachedMessages = new List<CachedMessage>();
            foreach (EventData message in messages)
            {
                StreamPosition position = this.dataAdapter.GetStreamPosition(this.Partition, message);
                cachedMessages.Add(this.dataAdapter.FromQueueMessage(position, message, dequeueTimeUtc, this.GetSegment));
                positions.Add(position);
            }
            var previousMetadataSize = cache.AllocatedSizeInBytes;
            cache.Add(cachedMessages, dequeueTimeUtc);
            UpdateMetadataMemory(previousMetadataSize);
            return positions;
        }

        /// <summary>
        /// Get a cursor into the cache to read events from a stream.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="sequenceToken">The position from which to begin reading.</param>
        /// <returns>A cache cursor.</returns>
        public object GetCursor(StreamId streamId, StreamSequenceToken? sequenceToken)
        {
            return cache.GetCursor(streamId, sequenceToken);
        }

        object IEventHubQueueCache.GetCursorAtPosition(StreamId streamId, StreamSubscriptionStartPosition startPosition)
        {
            return cache.GetCursorAtPosition(streamId, startPosition);
        }

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
        public bool TryGetNextMessage(object cursorObj, [NotNullWhen(true)] out IBatchContainer? message)
        {
            if (!cache.TryGetNextMessage(cursorObj, out message))
                return false;
            double cachePressureContribution;
            cachePressureMonitor.RecordCachePressureContribution(
                TryCalculateCachePressureContribution(message.SequenceToken, out cachePressureContribution)
                    ? cachePressureContribution
                    : 0.0);
            return true;
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
            if (lastItemPurged.HasValue)
            {
                checkpointer.Update(
                    this.dataAdapter.GetOffset(lastItemPurged.Value),
                    DateTime.UtcNow,
                    CancellationToken.None);
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
                FixedSizeBuffer newBuffer;
                if (bufferPool is IEventHubCacheBufferPool eventHubBufferPool)
                {
                    if (size > EventHubCacheBufferPool.MaxBufferSize)
                    {
                        throw new ArgumentOutOfRangeException(nameof(size), $"Message size is too big. MessageSize: {size}");
                    }

                    newBuffer = eventHubBufferPool.Allocate(Math.Max(size, preferredBufferSize));
                }
                else
                {
                    newBuffer = bufferPool.Allocate();
                }

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
                if (bufferPool is IEventHubCacheBufferPool)
                {
                    preferredBufferSize = Math.Min(currentBuffer.SizeInByte * 2, EventHubCacheBufferPool.MaxBufferSize);
                }
                //call EvictionStrategy's OnBlockAllocated method
                this.evictionStrategy.OnBlockAllocated(currentBuffer);
            }
            return segment;
        }

        private void UpdateMetadataMemory(long previousSize)
        {
            memoryController?.AdjustActiveMetadataMemory(cache.AllocatedSizeInBytes - previousSize);
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

    internal interface IMemoryPressureEvictionStrategy
    {
        void PerformMemoryPressurePurge(DateTime nowUtc);
    }
}
