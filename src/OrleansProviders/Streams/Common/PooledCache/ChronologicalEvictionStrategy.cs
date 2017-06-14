using System;
using System.Collections.Generic;
using Orleans.Runtime;
using System.Threading;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Eviction strategy that evicts data based off of age.
    /// </summary>
    /// <typeparam name="TCachedMessage"></typeparam>
    public abstract class ChronologicalEvictionStrategy<TCachedMessage> : IEvictionStrategy<TCachedMessage>
        where TCachedMessage : struct
    {
        private readonly Logger logger;
        private readonly TimePurgePredicate timePurge;
        /// <summary>
        /// Buffers which are currently in use in the cache
        /// Protected for test purposes
        /// </summary>
        protected readonly Queue<FixedSizeBuffer> inUseBuffers;
        private FixedSizeBuffer currentBuffer;
        private ICacheMonitor cacheMonitor;
        private Timer timer;
        private long cacheSizeInByte;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="timePurage"></param>
        /// <param name="cacheMonitor"></param>
        /// <param name="monitorWriteInterval"></param>
        protected ChronologicalEvictionStrategy(Logger logger, TimePurgePredicate timePurage, ICacheMonitor cacheMonitor, TimeSpan? monitorWriteInterval)
        {
            if (logger == null) throw new ArgumentException(nameof(logger));
            if (timePurage == null) throw new ArgumentException(nameof(timePurage));
            this.logger = logger;
            this.timePurge = timePurage;
            this.inUseBuffers = new Queue<FixedSizeBuffer>();
            this.cacheMonitor = cacheMonitor;
            if (cacheMonitor != null && monitorWriteInterval.HasValue)
            {
                this.timer = new Timer(this.ReportCacheSize, null, monitorWriteInterval.Value, monitorWriteInterval.Value);
            }
            this.cacheSizeInByte = 0;
        }

        private void ReportCacheSize(object state)
        {
            this.cacheMonitor.ReportCacheSize(this.cacheSizeInByte);
        }
        /// <summary>
        /// Get block pool block id for message
        /// </summary>
        /// <param name="cachedMessage"></param>
        /// <returns></returns>
        protected abstract object GetBlockId(TCachedMessage? cachedMessage);

        /// <summary>
        /// Get message enqueue time
        /// </summary>
        /// <param name="cachedMessage"></param>
        /// <returns></returns>
        protected abstract DateTime GetEnqueueTimeUtc(ref TCachedMessage cachedMessage);

        /// <summary>
        /// Get message dequeue time
        /// </summary>
        /// <param name="cachedMessage"></param>
        /// <returns></returns>
        protected abstract DateTime GetDequeueTimeUtc(ref TCachedMessage cachedMessage);

        /// <inheritdoc />
        public IPurgeObservable<TCachedMessage> PurgeObservable { private get; set; }

        /// <summary>
        /// Called with the newest item in the cache and last item purged after a cache purge has run.
        /// For ordered reliable queues we shouldn't need to notify on every purged event, only on the last event 
        ///   of every set of events that get purged.
        /// </summary>
        public Action<TCachedMessage?, TCachedMessage?> OnPurged { get; set; }

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
            //if the cache is empty, then nothing to purge, return
            if (this.PurgeObservable.IsEmpty)
                return;
            int itemsPurged = 0;
            TCachedMessage neweswtMessageInCache = this.PurgeObservable.Newest.Value;
            TCachedMessage? lastMessagePurged = null;
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

        private void FreePurgedBuffers(TCachedMessage? lastMessagePurged, TCachedMessage? oldestMessageInCache)
        {
            if (this.inUseBuffers.Count <= 0 || !lastMessagePurged.HasValue)
                return;
            int memoryReleasedInByte = 0;
            object IdOfLastPurgedBufferId = GetBlockId(lastMessagePurged);
            // IdOfLastBufferInCache will be null if cache is empty after purge
            object IdOfLastBufferInCacheId = GetBlockId(oldestMessageInCache);
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

        // Given a purge cached message, indicates whether it should be purged from the cache
        private bool ShouldPurge(ref TCachedMessage cachedMessage, ref TCachedMessage newestCachedMessage, DateTime nowUtc)
        {
            TimeSpan timeInCache = nowUtc - GetDequeueTimeUtc(ref cachedMessage);
            // age of message relative to the most recent event in the cache.
            TimeSpan relativeAge = GetEnqueueTimeUtc(ref newestCachedMessage) - GetEnqueueTimeUtc(ref cachedMessage);

            return timePurge.ShouldPurgFromTime(timeInCache, relativeAge);
        }

        /// <summary>
        /// Logs cache purge activity
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="purgeObservable"></param>
        /// <param name="itemsPurged"></param>
        private static void ReportPurge(Logger logger, IPurgeObservable<TCachedMessage> purgeObservable, int itemsPurged)
        {
            if (!logger.IsVerbose)
                return;
            int itemCountAfterPurge = purgeObservable.ItemCount;
            var itemCountBeforePurge = itemCountAfterPurge + itemsPurged;
            if (itemCountAfterPurge == 0)
            {
                logger.Verbose("BlockPurged: cache empty");
            }
            logger.Verbose($"BlockPurged: PurgeCount: {itemCountBeforePurge - itemCountAfterPurge}, CacheSize: {itemCountAfterPurge}");
        }
    }
}
