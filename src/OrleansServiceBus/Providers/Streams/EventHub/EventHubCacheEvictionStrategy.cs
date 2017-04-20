using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.ServiceBus.Providers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Eviction strategy for EventHubQueueCache
    /// </summary>
    public class EventHubCacheEvictionStrategy : IEvictionStrategy<CachedEventHubMessage>
    {
        //buffers which are still in use for current cache
        /// <summary>
        /// Buffers which are currently in use in the cache
        /// </summary>
        protected readonly Queue<FixedSizeBuffer> inUseBuffers;
        private readonly TimePurgePredicate timePurge;
        /// <summary>
        /// Buffers which are purged
        /// </summary>
        protected readonly Queue<FixedSizeBuffer> purgedBuffers;
        private readonly Logger logger;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="timePurage"></param>
        public EventHubCacheEvictionStrategy(Logger logger, TimePurgePredicate timePurage = null)
        {
            this.inUseBuffers = new Queue<FixedSizeBuffer>();
            this.logger = logger.GetSubLogger(this.GetType().Name);
            this.purgedBuffers = new Queue<FixedSizeBuffer>();
            this.timePurge = timePurage ?? TimePurgePredicate.Default;
        }
        /// <inheritdoc />
        public IPurgeObservable<CachedEventHubMessage> PurgeObservable { private get; set; }

        /// <summary>
        /// Called with the newest item in the cache and last item purged after a cache purge has run.
        /// For ordered reliable queues we shouldn't need to notify on every purged event, only on the last event 
        ///   of every set of events that get purged.
        /// </summary>
        public Action<CachedEventHubMessage?, CachedEventHubMessage?> OnPurged { get; set; }

        /// <inheritdoc />
        public void OnBlockAllocated(IDisposable newBlock)
        {
            var newBuffer = newBlock as FixedSizeBuffer;
            this.inUseBuffers.Enqueue(newBuffer);
            newBuffer.SetPurgeAction(this.OnFreeBlockRequest);
        }

        /// <inheritdoc />
        public void PerformPurge(DateTime nowUtc, IDisposable purgeRequest)
        {
            //if the cache is empty, then nothing to purge, return
            if (this.PurgeObservable.IsEmpty)
                return;
            var itemCountBeforePurge = this.PurgeObservable.ItemCount;
            int itemsPurged = 0;
            CachedEventHubMessage neweswtMessageInCache = this.PurgeObservable.Newest.Value;
            CachedEventHubMessage? lastMessagePurged = null;
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
            var itemCountAfterPurge = itemCountBeforePurge - itemsPurged;

            //purge finished, time to conduct follow up actions 
            OnPurged?.Invoke(lastMessagePurged, this.PurgeObservable.Newest);
            UpdatePurgedBuffers(lastMessagePurged, this.PurgeObservable.Oldest, itemsPurged > 0);
            ReportPurge(itemCountBeforePurge, itemCountAfterPurge);
        }

        private void UpdatePurgedBuffers(CachedEventHubMessage? lastMessagePurged, CachedEventHubMessage? oldestMessageInCache, bool itemsGotPurged)
        {
            //if nothing purged, then no buffer was purged
            //if items got purged, then potencially some buffer was purged
            if (itemsGotPurged)
            {
                var IdOfLastPurgedBuffer = lastMessagePurged.Value.Segment.Array;
                // IdOfLastBufferInCache will be null if cache is empty after purge
                var IdOfLastBufferInCache = oldestMessageInCache.HasValue ? oldestMessageInCache.Value.Segment.Array : null;
                //all buffer older than LastPurgedBuffer should be purged 
                while (this.inUseBuffers.Peek().Id != IdOfLastPurgedBuffer)
                {
                    this.purgedBuffers.Enqueue(this.inUseBuffers.Dequeue());
                }
                // if last purged message does not share buffer with remaining messages in cache, or cache is empty after purge, 
                //then last purged buffer should be in purgedBuffers too
                if (IdOfLastBufferInCache == null || IdOfLastPurgedBuffer != IdOfLastBufferInCache)
                {
                   this.purgedBuffers.Enqueue(this.inUseBuffers.Dequeue());
                }
            }
        }

        // Given a purge cached message, indicates whether it should be purged from the cache
        private bool ShouldPurge(ref CachedEventHubMessage cachedMessage, ref CachedEventHubMessage newestCachedMessage, DateTime nowUtc)
        {
            TimeSpan timeInCache = nowUtc - cachedMessage.DequeueTimeUtc;
            // age of message relative to the most recent event in the cache.
            TimeSpan relativeAge = newestCachedMessage.EnqueueTimeUtc - cachedMessage.EnqueueTimeUtc;

            return timePurge.ShouldPurgFromTime(timeInCache, relativeAge);
        }

        private void OnFreeBlockRequest(IDisposable block)
        {
            var purgeCandidate = block as FixedSizeBuffer;
            //free all blocks before purgeCandidate and purgeCandidate
            if (this.purgedBuffers.Contains(purgeCandidate))
            {
                while (this.purgedBuffers.Peek() != purgeCandidate)
                    this.purgedBuffers.Dequeue().Dispose();
                purgeCandidate.Dispose();
            }
        }

        private void ReportPurge(int itemCountBeforePurge, int itemCountAfterPurge)
        {
            if (itemCountAfterPurge == 0)
            {
                logger.Verbose("BlockPurged: cache empty");
            }
            logger.Verbose($"BlockPurged: PurgeCount: {itemCountBeforePurge - itemCountAfterPurge}, CacheSize: {itemCountAfterPurge}");
        }
    }
}
