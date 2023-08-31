using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Eviction strategy that evicts data based off of age.
    /// </summary>
    public class ChronologicalEvictionStrategy : IEvictionStrategy
    {
        private readonly ILogger logger;
        private readonly TimePurgePredicate timePurge;

        /// <summary>
        /// Buffers which are currently in use in the cache
        /// Protected for test purposes
        /// </summary>
        protected readonly Queue<FixedSizeBuffer> inUseBuffers;
        private FixedSizeBuffer currentBuffer;
        private readonly ICacheMonitor cacheMonitor;
        private readonly PeriodicAction periodicMonitoring;
        private long cacheSizeInByte;
        
        /// <summary>
        /// Initializes a new instance of the <see cref="ChronologicalEvictionStrategy"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="timePurage">The time-based purge predicate.</param>
        /// <param name="cacheMonitor">The cache monitor.</param>
        /// <param name="monitorWriteInterval">"Interval to write periodic statistics. Only triggered for active caches.</param>
        public ChronologicalEvictionStrategy(ILogger logger, TimePurgePredicate timePurage, ICacheMonitor cacheMonitor, TimeSpan? monitorWriteInterval)
        {
            if (logger == null) throw new ArgumentException(nameof(logger));
            if (timePurage == null) throw new ArgumentException(nameof(timePurage));
            this.logger = logger;
            timePurge = timePurage;
            inUseBuffers = new Queue<FixedSizeBuffer>();

            // monitoring
            this.cacheMonitor = cacheMonitor;
            if (this.cacheMonitor != null && monitorWriteInterval.HasValue)
            {
                periodicMonitoring = new PeriodicAction(monitorWriteInterval.Value, ReportCacheSize);
            }

            cacheSizeInByte = 0;
        }

        private void ReportCacheSize()
        {
            cacheMonitor.ReportCacheSize(cacheSizeInByte);
        }

        /// <inheritdoc />
        public IPurgeObservable PurgeObservable { private get; set; }

        /// <inheritdoc />
        public Action<CachedMessage?, CachedMessage?> OnPurged { get; set; }

        /// <inheritdoc />
        public void OnBlockAllocated(FixedSizeBuffer newBlock)
        {
            if (PurgeObservable.IsEmpty && currentBuffer != null
                && inUseBuffers.Contains(currentBuffer) && inUseBuffers.Count == 1)
            {
                inUseBuffers.Dequeue().Dispose();
            }
            inUseBuffers.Enqueue(newBlock);
            currentBuffer = newBlock;
            //report metrics
            cacheSizeInByte += newBlock.SizeInByte;
            cacheMonitor?.TrackMemoryAllocated(newBlock.SizeInByte);
        }

        /// <inheritdoc />
        public void PerformPurge(DateTime nowUtc)
        {
            PerformPurgeInternal(nowUtc);
            periodicMonitoring?.TryAction(nowUtc);
        }

        /// <summary>
        /// Given a cached message, indicates whether it should be purged from the cache.
        /// </summary>
        /// <param name="cachedMessage">The cached message.</param>
        /// <param name="newestCachedMessage">The newest cached message.</param>
        /// <param name="nowUtc">The current time (UTC).</param>
        /// <returns><see langword="true" /> if the message should be purged, <see langword="false" /> otherwise.</returns>
        protected virtual bool ShouldPurge(ref CachedMessage cachedMessage, ref CachedMessage newestCachedMessage, DateTime nowUtc)
        {
            TimeSpan timeInCache = nowUtc - cachedMessage.DequeueTimeUtc;
            // age of message relative to the most recent event in the cache.
            TimeSpan relativeAge = newestCachedMessage.EnqueueTimeUtc - cachedMessage.EnqueueTimeUtc;

            return timePurge.ShouldPurgeFromTime(timeInCache, relativeAge);
        }

        private void PerformPurgeInternal(DateTime nowUtc)
        {
            //if the cache is empty, then nothing to purge, return
            if (PurgeObservable.IsEmpty)
                return;
            int itemsPurged = 0;
            CachedMessage neweswtMessageInCache = PurgeObservable.Newest.Value;
            CachedMessage? lastMessagePurged = null;
            while (!PurgeObservable.IsEmpty)
            {
                var oldestMessageInCache = PurgeObservable.Oldest.Value;
                if (!ShouldPurge(ref oldestMessageInCache, ref neweswtMessageInCache, nowUtc))
                {
                    break;
                }
                lastMessagePurged = oldestMessageInCache;
                itemsPurged++;
                PurgeObservable.RemoveOldestMessage();
            }
            //if nothing got purged, return
            if (itemsPurged == 0)
                return;

            //items got purged, time to conduct follow up actions 
            cacheMonitor?.TrackMessagesPurged(itemsPurged);
            OnPurged?.Invoke(lastMessagePurged, PurgeObservable.Newest);
            FreePurgedBuffers(lastMessagePurged, PurgeObservable.Oldest);
            ReportPurge(logger, PurgeObservable, itemsPurged);
        }

        private void FreePurgedBuffers(CachedMessage? lastMessagePurged, CachedMessage? oldestMessageInCache)
        {
            if (inUseBuffers.Count <= 0 || !lastMessagePurged.HasValue)
                return;
            int memoryReleasedInByte = 0;
            object IdOfLastPurgedBufferId = lastMessagePurged?.Segment.Array;
            // IdOfLastBufferInCache will be null if cache is empty after purge
            object IdOfLastBufferInCacheId = oldestMessageInCache?.Segment.Array;
            //all buffers older than LastPurgedBuffer should be purged 
            while (inUseBuffers.Peek().Id != IdOfLastPurgedBufferId)
            {
                var purgedBuffer = inUseBuffers.Dequeue();
                memoryReleasedInByte += purgedBuffer.SizeInByte;
                purgedBuffer.Dispose();
            }
            // if last purged message does not share buffer with remaining messages in cache and cache is not empty
            //then last purged buffer should be purged too
            if (IdOfLastBufferInCacheId != null && IdOfLastPurgedBufferId != IdOfLastBufferInCacheId)
            {
                var purgedBuffer = inUseBuffers.Dequeue();
                memoryReleasedInByte += purgedBuffer.SizeInByte;
                purgedBuffer.Dispose();
            }
            //report metrics
            if (memoryReleasedInByte > 0)
            {
                cacheSizeInByte -= memoryReleasedInByte;
                cacheMonitor?.TrackMemoryReleased(memoryReleasedInByte);
            }
        }

        /// <summary>
        /// Logs cache purge activity
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="purgeObservable">The purge observable.</param>
        /// <param name="itemsPurged">The items purged.</param>
        private static void ReportPurge(ILogger logger, IPurgeObservable purgeObservable, int itemsPurged)
        {
            if (!logger.IsEnabled(LogLevel.Debug))
                return;
            int itemCountAfterPurge = purgeObservable.ItemCount;
            var itemCountBeforePurge = itemCountAfterPurge + itemsPurged;
            if (itemCountAfterPurge == 0)
            {
                logger.LogDebug("BlockPurged: cache empty");
            }
            logger.LogDebug("BlockPurged: PurgeCount: {PurgeCount}, CacheSize: {ItemCountAfterPurge}", itemCountBeforePurge - itemCountAfterPurge, itemCountAfterPurge);
        }
    }
}
