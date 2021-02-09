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
        /// Constructor
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="timePurage"></param>
        /// <param name="cacheMonitor"></param>
        /// <param name="monitorWriteInterval">"Interval to write periodic statistics.  Only triggered for active caches.</param>
        public ChronologicalEvictionStrategy(ILogger logger, TimePurgePredicate timePurage, ICacheMonitor cacheMonitor, TimeSpan? monitorWriteInterval)
        {
            if (logger == null) throw new ArgumentException(nameof(logger));
            if (timePurage == null) throw new ArgumentException(nameof(timePurage));
            this.logger = logger;
            this.timePurge = timePurage;
            this.inUseBuffers = new Queue<FixedSizeBuffer>();

            // monitoring
            this.cacheMonitor = cacheMonitor;
            if (this.cacheMonitor != null && monitorWriteInterval.HasValue)
            {
                this.periodicMonitoring = new PeriodicAction(monitorWriteInterval.Value, this.ReportCacheSize);
            }

            this.cacheSizeInByte = 0;
        }

        private void ReportCacheSize()
        {
            this.cacheMonitor.ReportCacheSize(this.cacheSizeInByte);
        }

        /// <inheritdoc />
        public IPurgeObservable PurgeObservable { private get; set; }

        /// <summary>
        /// Called with the newest item in the cache and last item purged after a cache purge has run.
        /// For ordered reliable queues we shouldn't need to notify on every purged event, only on the last event 
        ///   of every set of events that get purged.
        /// </summary>
        public Action<CachedMessage?, CachedMessage?> OnPurged { get; set; }

        /// <inheritdoc />
        public void OnBlockAllocated(FixedSizeBuffer newBlock)
        {
            if (this.PurgeObservable.IsEmpty && this.currentBuffer != null
                && this.inUseBuffers.Contains(this.currentBuffer) && this.inUseBuffers.Count == 1)
            {
                this.inUseBuffers.Dequeue().Dispose();
            }
            this.inUseBuffers.Enqueue(newBlock);
            this.currentBuffer = newBlock;
            //report metrics
            this.cacheSizeInByte += newBlock.SizeInByte;
            this.cacheMonitor?.TrackMemoryAllocated(newBlock.SizeInByte);
        }

        /// <inheritdoc />
        public void PerformPurge(DateTime nowUtc)
        {
            PerformPurgeInternal(nowUtc);
            this.periodicMonitoring?.TryAction(nowUtc);
        }

        /// <summary>
        /// Given a purge cached message, indicates whether it should be purged from the cache.
        /// </summary>
        protected virtual bool ShouldPurge(ref CachedMessage cachedMessage, ref CachedMessage newestCachedMessage, DateTime nowUtc)
        {
            TimeSpan timeInCache = nowUtc - cachedMessage.DequeueTimeUtc;
            // age of message relative to the most recent event in the cache.
            TimeSpan relativeAge = newestCachedMessage.EnqueueTimeUtc - cachedMessage.EnqueueTimeUtc;

            return timePurge.ShouldPurgFromTime(timeInCache, relativeAge);
        }

        private void PerformPurgeInternal(DateTime nowUtc)
        {
            //if the cache is empty, then nothing to purge, return
            if (this.PurgeObservable.IsEmpty)
                return;
            int itemsPurged = 0;
            CachedMessage neweswtMessageInCache = this.PurgeObservable.Newest.Value;
            CachedMessage? lastMessagePurged = null;
            while (!this.PurgeObservable.IsEmpty)
            {
                var oldestMessageInCache = this.PurgeObservable.Oldest.Value;
                if (!ShouldPurge(ref oldestMessageInCache, ref neweswtMessageInCache, nowUtc))
                {
                    break;
                }
                lastMessagePurged = oldestMessageInCache;
                itemsPurged++;
                this.PurgeObservable.RemoveOldestMessage();
            }
            //if nothing got purged, return
            if (itemsPurged == 0)
                return;

            //items got purged, time to conduct follow up actions 
            this.cacheMonitor?.TrackMessagesPurged(itemsPurged);
            OnPurged?.Invoke(lastMessagePurged, this.PurgeObservable.Newest);
            FreePurgedBuffers(lastMessagePurged, this.PurgeObservable.Oldest);
            ReportPurge(this.logger, this.PurgeObservable, itemsPurged);
        }

        private void FreePurgedBuffers(CachedMessage? lastMessagePurged, CachedMessage? oldestMessageInCache)
        {
            if (this.inUseBuffers.Count <= 0 || !lastMessagePurged.HasValue)
                return;
            int memoryReleasedInByte = 0;
            object IdOfLastPurgedBufferId = lastMessagePurged?.Segment.Array;
            // IdOfLastBufferInCache will be null if cache is empty after purge
            object IdOfLastBufferInCacheId = oldestMessageInCache?.Segment.Array;
            //all buffers older than LastPurgedBuffer should be purged 
            while (this.inUseBuffers.Peek().Id != IdOfLastPurgedBufferId)
            {
                var purgedBuffer = this.inUseBuffers.Dequeue();
                memoryReleasedInByte += purgedBuffer.SizeInByte;
                purgedBuffer.Dispose();
            }
            // if last purged message does not share buffer with remaining messages in cache and cache is not empty
            //then last purged buffer should be purged too
            if (IdOfLastBufferInCacheId != null && IdOfLastPurgedBufferId != IdOfLastBufferInCacheId)
            {
                var purgedBuffer = this.inUseBuffers.Dequeue();
                memoryReleasedInByte += purgedBuffer.SizeInByte;
                purgedBuffer.Dispose();
            }
            //report metrics
            if (memoryReleasedInByte > 0)
            {
                this.cacheSizeInByte -= memoryReleasedInByte;
                this.cacheMonitor?.TrackMemoryReleased(memoryReleasedInByte);
            }
        }

        /// <summary>
        /// Logs cache purge activity
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="purgeObservable"></param>
        /// <param name="itemsPurged"></param>
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
