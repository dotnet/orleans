using System;
using System.Collections.Generic;
using Orleans.Providers.Abstractions;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Eviction strategy that evicts data based off of age.
    /// </summary>
    public class ChronologicalEvictionStrategy : IFiFoEvictionStrategy<CachedMessage>
    {
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

        public ChronologicalEvictionStrategy(TimePurgePredicate timePurge, ICacheMonitor cacheMonitor, TimeSpan? monitorWriteInterval)
        {
            this.timePurge = timePurge ?? throw new ArgumentException(nameof(timePurge));
            this.inUseBuffers = new Queue<FixedSizeBuffer>();

            // monitoring
            this.cacheMonitor = cacheMonitor;
            if (this.cacheMonitor != null && monitorWriteInterval.HasValue)
            {
                this.periodicMonitoring = new PeriodicAction(monitorWriteInterval.Value, this.ReportCacheSize);
            }

            this.cacheSizeInByte = 0;
        }

        private void ReportCacheSize() => this.cacheMonitor.ReportCacheSize(this.cacheSizeInByte);

        /// <inheritdoc />
        public void OnBlockAllocated(FixedSizeBuffer newBlock)
        {
            if(this.currentBuffer != null)
            {
                this.inUseBuffers.Enqueue(this.currentBuffer);
            }
            this.currentBuffer = newBlock;
            //report metrics
            this.cacheSizeInByte += newBlock.SizeInByte;
            this.cacheMonitor?.TrackMemoryAllocated(newBlock.SizeInByte);
        }

        /// <inheritdoc />
        public bool TryEvict(IFiFoEvictableCache<CachedMessage> cache, in DateTime nowUtc)
        {
            bool itemsWerePurged = this.PerformPurgeInternal(cache, nowUtc);
            this.periodicMonitoring?.TryAction(nowUtc);
            return itemsWerePurged;
        }

        private bool PerformPurgeInternal(IFiFoEvictableCache<CachedMessage> cache, in DateTime nowUtc)
        {
            //if the cache is empty, then nothing to purge, return
            if (!cache.Oldest.HasValue) return false;

            int itemsPurged = 0;
            CachedMessage neweswtMessageInCache = cache.Newest.Value;
            CachedMessage? lastMessagePurged = null;
            while (cache.Oldest.HasValue)
            {
                var oldestMessageInCache = cache.Oldest.Value;
                if (!this.ShouldPurge(ref oldestMessageInCache, ref neweswtMessageInCache, nowUtc))
                {
                    break;
                }
                lastMessagePurged = oldestMessageInCache;
                itemsPurged++;
                cache.RemoveOldestMessage();
            }

            //if nothing got purged, return
            if (itemsPurged == 0) return false;

            //items got purged, time to conduct follow up actions 
            this.cacheMonitor?.TrackMessagesPurged(itemsPurged);
            this.FreePurgedBuffers(lastMessagePurged, cache.Oldest);

            return true;
        }

        private void FreePurgedBuffers(CachedMessage? lastMessagePurged, CachedMessage? oldestMessageInCache)
        {
            if (this.inUseBuffers.Count <= 0 || !lastMessagePurged.HasValue)
                return;
            int memoryReleasedInBytes = 0;
            object IdOfLastPurgedBufferId = lastMessagePurged?.Id;
            // IdOfLastBufferInCache will be null if cache is empty after purge
            //all buffers older than LastPurgedBuffer should be purged 
            while (this.inUseBuffers.Count > 0 && this.inUseBuffers.Peek().Id != IdOfLastPurgedBufferId)
            {
                var purgedBuffer = this.inUseBuffers.Dequeue();
                memoryReleasedInBytes += purgedBuffer.SizeInByte;
                purgedBuffer.Dispose();
            }
            // if last purged message does not share buffer with remaining messages in cache and cache is not empty
            //then last purged buffer should be purged too
            object IdOfLastBufferInCacheId = oldestMessageInCache?.Id;
            if (IdOfLastBufferInCacheId != null && IdOfLastPurgedBufferId != IdOfLastBufferInCacheId)
            {
                var purgedBuffer = this.inUseBuffers.Dequeue();
                memoryReleasedInBytes += purgedBuffer.SizeInByte;
                purgedBuffer.Dispose();
            }
            //report metrics
            if (memoryReleasedInBytes > 0)
            {
                this.cacheSizeInByte -= memoryReleasedInBytes;
                this.cacheMonitor?.TrackMemoryReleased(memoryReleasedInBytes);
            }
        }

        // Given a purge cached message, indicates whether it should be purged from the cache
        private bool ShouldPurge(ref CachedMessage cachedMessage, ref CachedMessage newestCachedMessage, in DateTime nowUtc)
        {
            TimeSpan timeInCache = nowUtc - cachedMessage.DequeueTimeUtc;
            // age of message relative to the most recent event in the cache.
            TimeSpan relativeAge =  newestCachedMessage.EnqueueTimeUtc - cachedMessage.EnqueueTimeUtc;

            return this.timePurge.ShouldPurgFromTime(timeInCache, relativeAge);
        }
    }
}
