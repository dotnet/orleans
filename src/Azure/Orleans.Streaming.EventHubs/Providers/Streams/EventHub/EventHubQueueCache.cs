using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.EventHubs;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Providers.Abstractions;
using Orleans.Configuration;
using System.Linq;

namespace Orleans.ServiceBus.Providers
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
        private readonly EventHubDataAdapter dataAdapter;
        private readonly IFiFoEvictionStrategy<CachedMessage> evictionStrategy;
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
        public EventHubQueueCache(
            string partition,
            int defaultMaxAddCount,
            IObjectPool<FixedSizeBuffer> bufferPool,
            EventHubDataAdapter dataAdapter,
            IFiFoEvictionStrategy<CachedMessage> evictionStrategy,
            IStreamQueueCheckpointer<string> checkpointer,
            ILogger logger,
            ICacheMonitor cacheMonitor,
            TimeSpan? cacheMonitorWriteInterval)
        {
            this.Partition = partition;
            this.defaultMaxAddCount = defaultMaxAddCount;
            this.bufferPool = bufferPool;
            this.dataAdapter = dataAdapter;
            this.checkpointer = checkpointer;
            this.cache = new PooledQueueCache(cacheMonitor, cacheMonitorWriteInterval);
            this.cacheMonitor = cacheMonitor;
            this.evictionStrategy = evictionStrategy;
            this.cachePressureMonitor = new AggregatedCachePressureMonitor(logger, cacheMonitor);
            this.logger = logger;
        }

        /// <inheritdoc />
        public void SignalPurge()
        {
            DateTime nowUtc = DateTime.UtcNow;
            if(this.evictionStrategy.TryEvict(this.cache, nowUtc))
            {
                if (this.logger.IsEnabled(LogLevel.Debug) && this.cache.Oldest.HasValue && this.cache.Newest.HasValue)
                {
                    this.logger.Debug("CachePeriod: EnqueueTimeUtc: {OldestEnqueueTimeUtc} to {NewestEnqueueTimeUtc}, DequeueTimeUtc: {OldestDequeueTimeUtc} to {NewestDequeueTimeUtc}",
                        LogFormatter.PrintDate(this.cache.Oldest.Value.EnqueueTimeUtc),
                        LogFormatter.PrintDate(this.cache.Newest.Value.EnqueueTimeUtc),
                        LogFormatter.PrintDate(this.cache.Oldest.Value.DequeueTimeUtc),
                        LogFormatter.PrintDate(this.cache.Newest.Value.DequeueTimeUtc));
                }
                if (this.cache.Oldest.HasValue)
                {
                    this.checkpointer.Update(EventHubDataAdapter.TokenToOffset(this.cache.Oldest.Value.OffsetToken().ToArray()), nowUtc);
                }
            }
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
        /// The limit of the maximum number of items that can be added
        /// </summary>
        public int GetMaxAddCount() => this.cachePressureMonitor.IsUnderPressure(DateTime.UtcNow)
            ? 0
            : this.defaultMaxAddCount;

        /// <summary>
        /// Add a list of EventHub EventData to the cache.
        /// </summary>
        /// <param name="messages"></param>
        /// <param name="dequeueTimeUtc"></param>
        /// <returns></returns>
        public List<StreamPosition> Add(List<EventData> messages, in DateTime dequeueTimeUtc)
        {
            List<StreamPosition> positions = new List<StreamPosition>();
            List<CachedMessage> cachedMessages = new List<CachedMessage>();
            foreach (EventData message in messages)
            {
                IQueueMessageCacheAdapter messageAdapter = this.dataAdapter.Create(this.Partition, message);
                StreamPosition position = messageAdapter.StreamPosition;
                cachedMessages.Add(CachedMessage.Create(messageAdapter, dequeueTimeUtc, this.GetSegment));
                positions.Add(position);
            }
            this.cache.Add(cachedMessages, dequeueTimeUtc);
            return positions;
        }

        /// <summary>
        /// Get a cursor into the cache to read events from a stream.
        /// </summary>
        /// <param name="streamIdentity"></param>
        /// <param name="sequenceToken"></param>
        /// <returns></returns>
        public object GetCursor(IStreamIdentity streamIdentity, StreamSequenceToken sequenceToken)
        {
            return this.cache.GetCursor(StreamIdentityToken.Create(streamIdentity), sequenceToken?.SequenceToken);
        }

        /// <summary>
        /// Try to get the next message in the cache for the provided cursor.
        /// </summary>
        /// <param name="cursorObj"></param>
        /// <param name="message"></param>
        /// <returns></returns>
        public bool TryGetNextMessage(object cursorObj, out IBatchContainer message)
        {
            message = default;
            if (!cache.TryGetNextMessage(cursorObj, out CachedMessage cachedMessage))
                return false;
            message = this.dataAdapter.GetBatchContainer(cachedMessage);
            _ = this.TryCalculateCachePressureContribution(cachedMessage, out double cachePressureContribution);
            this.cachePressureMonitor.RecordCachePressureContribution(cachePressureContribution);
            return true;
        }

        /// <summary>
        /// cachePressureContribution should be a double between 0-1, indicating how much danger the item is of being removed from the cache.
        ///   0 indicating  no danger,
        ///   1 indicating removal is imminent.
        /// </summary>
        private bool TryCalculateCachePressureContribution(in CachedMessage cachedMessage, out double cachePressureContribution)
        {
            cachePressureContribution = 0;
            // if cache is empty or has few items, don't calculate pressure
            if (!this.cache.Newest.HasValue ||
                !this.cache.Oldest.HasValue ||
                (this.cache.Newest.Value.DequeueTimeUtc - this.cache.Oldest.Value.DequeueTimeUtc).TotalSeconds < StreamCacheEvictionOptions.MinDataMinTimeInCache.TotalSeconds) // not enough items in cache.
            {
                return false;
            }

            var cacheRealTimeAgeDelta = this.cache.Newest.Value.DequeueTimeUtc - this.cache.Oldest.Value.DequeueTimeUtc;
            var distanceFromNewestMessage = this.cache.Newest.Value.DequeueTimeUtc - cachedMessage.DequeueTimeUtc;
            // pressure is the ratio of the distance from the front of the cache to the
            cachePressureContribution = distanceFromNewestMessage.Ticks / Math.Max(1, cacheRealTimeAgeDelta.Ticks);

            return true;
        }

        private ArraySegment<byte> GetSegment(int size)
        {
            // get segment from current block
            ArraySegment<byte> segment;
            if (this.currentBuffer == null || !this.currentBuffer.TryGetSegment(size, out segment))
            {
                // no block or block full, get new block and try again
                this.currentBuffer = this.bufferPool.Allocate();
                //call EvictionStrategy's OnBlockAllocated method
                this.evictionStrategy.OnBlockAllocated(this.currentBuffer);
                // if this fails with clean block, then requested size is too big
                if (!this.currentBuffer.TryGetSegment(size, out segment))
                {
                    throw new ArgumentOutOfRangeException(nameof(size), $"Message size is to big. MessageSize: {size}");
                }
            }
            return segment;
        }
    }
}
