using System;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Azure.Messaging.EventHubs;

namespace Orleans.Streaming.EventHubs
{
    /// <summary>
    /// EventHub queue cache
    /// </summary>
    public class EventHubQueueCache : IEventHubQueueCache
    {
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
        private readonly ICacheMonitor cacheMonitor;
        private FixedSizeBuffer currentBuffer;

        /// <summary>
        /// EventHub queue cache.
        /// </summary>
        /// <param name="partition">Partition this instance is caching.</param>
        /// <param name="defaultMaxAddCount">Default max number of items that can be added to the cache between purge calls.</param>
        /// <param name="bufferPool">raw data block pool.</param>
        /// <param name="dataAdapter">Adapts EventData to cached.</param>
        /// <param name="evictionStrategy">Eviction strategy manage purge related events</param>
        /// <param name="checkpointer">Logic used to store queue position.</param>
        /// <param name="logger"></param>
        /// <param name="cacheMonitor"></param>
        /// <param name="cacheMonitorWriteInterval"></param>
        /// <param name="metadataMinTimeInCache"></param>
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
            this.evictionStrategy.PurgeObservable = this.cache;
            this.cachePressureMonitor = new AggregatedCachePressureMonitor(logger, cacheMonitor);
            this.logger = logger;
        }

        /// <inheritdoc />
        public void SignalPurge()
        {
            this.evictionStrategy.PerformPurge(DateTime.UtcNow);
        }

        /// <summary>
        /// Add cache pressure monitor to the cache's back pressure algorithm
        /// </summary>
        /// <param name="monitor"></param>
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
        public int GetMaxAddCount()
        {
            return cachePressureMonitor.IsUnderPressure(DateTime.UtcNow) ? 0 : defaultMaxAddCount;
        }

        /// <summary>
        /// Add a list of EventHub EventData to the cache.
        /// </summary>
        /// <param name="messages"></param>
        /// <param name="dequeueTimeUtc"></param>
        /// <returns></returns>
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
            cache.Add(cachedMessages, dequeueTimeUtc);
            return positions;
        }

        /// <summary>
        /// Get a cursor into the cache to read events from a stream.
        /// </summary>
        /// <param name="streamId"></param>
        /// <param name="sequenceToken"></param>
        /// <returns></returns>
        public object GetCursor(StreamId streamId, StreamSequenceToken sequenceToken)
        {
            return cache.GetCursor(streamId, sequenceToken);
        }

        /// <summary>
        /// Try to get the next message in the cache for the provided cursor.
        /// </summary>
        /// <param name="cursorObj"></param>
        /// <param name="message"></param>
        /// <returns></returns>
        public bool TryGetNextMessage(object cursorObj, out IBatchContainer message)
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
            if (logger.IsEnabled(LogLevel.Debug) && lastItemPurged.HasValue && newestItem.HasValue)
            {
                logger.LogDebug(
                    "CachePeriod: EnqueueTimeUtc: {OldestEnqueueTimeUtc} to {NewestEnqueueTimeUtc}, DequeueTimeUtc: {OldestDequeueTimeUtc} to {NewestDequeueTimeUtc}",
                    LogFormatter.PrintDate(lastItemPurged.Value.EnqueueTimeUtc),
                    LogFormatter.PrintDate(newestItem.Value.EnqueueTimeUtc),
                    LogFormatter.PrintDate(lastItemPurged.Value.DequeueTimeUtc),
                    LogFormatter.PrintDate(newestItem.Value.DequeueTimeUtc));
            }
            if (lastItemPurged.HasValue)
            {
                checkpointer.Update(this.dataAdapter.GetOffset(lastItemPurged.Value), DateTime.UtcNow);
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
                currentBuffer = bufferPool.Allocate();
                //call EvictionStrategy's OnBlockAllocated method
                this.evictionStrategy.OnBlockAllocated(currentBuffer);
                // if this fails with clean block, then requested size is too big
                if (!currentBuffer.TryGetSegment(size, out segment))
                {
                    throw new ArgumentOutOfRangeException(nameof(size), $"Message size is to big. MessageSize: {size}");
                }
            }
            return segment;
        }
    }
}
