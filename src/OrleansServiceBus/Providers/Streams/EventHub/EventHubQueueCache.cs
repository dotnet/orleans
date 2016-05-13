
using System;
using Microsoft.ServiceBus.Messaging;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// EventHub queue cache that allows developers to provide their own cached data structure.
    /// </summary>
    /// <typeparam name="TCachedMessage"></typeparam>
    public abstract class EventHubQueueCache<TCachedMessage> : IEventHubQueueCache
        where TCachedMessage : struct
    {
        protected readonly int defaultMaxAddCount;
        protected readonly PooledQueueCache<EventData, TCachedMessage> cache;
        private readonly AveragingCachePressureMonitor cachePressureMonitor;

        protected IStreamQueueCheckpointer<string> Checkpointer { get; }

        protected EventHubQueueCache(int defaultMaxAddCount, IStreamQueueCheckpointer<string> checkpointer, ICacheDataAdapter<EventData, TCachedMessage> cacheDataAdapter, ICacheDataComparer<TCachedMessage> comparer)
        {
            this.defaultMaxAddCount = defaultMaxAddCount;
            Checkpointer = checkpointer;

            cache = new PooledQueueCache<EventData, TCachedMessage>(cacheDataAdapter, comparer);
            cacheDataAdapter.PurgeAction = cache.Purge;
            cache.OnPurged = CheckpointOnPurged;

            cachePressureMonitor = new AveragingCachePressureMonitor();
        }

        protected abstract string GetOffset(TCachedMessage lastItemPurged);



        /// <summary>
        /// cachePressureContribution should be a double between 0-1, indicating how much danger the item is of being removed from the cache.
        ///   0 indicating  no danger,
        ///   1 indicating removal is imminent.
        /// </summary>
        /// <param name="token"></param>
        /// <param name="cachePressureContribution"></param>
        /// <returns></returns>
        protected abstract bool TryCalculateCachePressureContribution(StreamSequenceToken token, out double cachePressureContribution);

        public void Dispose()
        {
            cache.OnPurged = null;
        }

        private void CheckpointOnPurged(TCachedMessage lastItemPurged)
        {
            Checkpointer.Update(GetOffset(lastItemPurged), DateTime.UtcNow);
        }

        public int GetMaxAddCount()
        {
            return cachePressureMonitor.IsUnderPressure() ? 0 : defaultMaxAddCount;
        }

        public StreamPosition Add(EventData message, DateTime dequeueTimeUtc)
        {
            return cache.Add(message, dequeueTimeUtc);
        }

        public object GetCursor(IStreamIdentity streamIdentity, StreamSequenceToken sequenceToken)
        {
            return cache.GetCursor(streamIdentity, sequenceToken);
        }

        public bool TryGetNextMessage(object cursorObj, out IBatchContainer message)
        {
            if (!cache.TryGetNextMessage(cursorObj, out message))
                return false;
            double cachePressureContribution;
            if (TryCalculateCachePressureContribution(message.SequenceToken, out cachePressureContribution))
            {
                cachePressureMonitor.RecordCachePressureContribution(cachePressureContribution);
            }
            return true;
        }

        private class AveragingCachePressureMonitor
        {
            const double pressureThreshold = 1.0/3.0;

            private double accumulatedCachePressure;
            private int cachePressureContributionCount;

            public void RecordCachePressureContribution(double cachePressureContribution)
            {
                accumulatedCachePressure += cachePressureContribution;
                cachePressureContributionCount++;
            }

            public bool IsUnderPressure()
            {
                if (cachePressureContributionCount == 0)
                    return false;

                double pressure = accumulatedCachePressure/cachePressureContributionCount;

                cachePressureContributionCount = 0;
                accumulatedCachePressure = 0;

                return pressure > pressureThreshold;
            }
        }
    }

    /// <summary>
    /// Message cache that stores EventData as a CachedEventHubMessage in a pooled message cache
    /// </summary>
    public class EventHubQueueCache : EventHubQueueCache<CachedEventHubMessage>
    {
        public EventHubQueueCache(IStreamQueueCheckpointer<string> checkpointer, IObjectPool<FixedSizeBuffer> bufferPool)
            : this(checkpointer, new EventHubDataAdapter(bufferPool))
        {
        }

        public EventHubQueueCache(IStreamQueueCheckpointer<string> checkpointer, ICacheDataAdapter<EventData, CachedEventHubMessage> cacheDataAdapter)
            : base(EventHubAdapterReceiver.MaxMessagesPerRead, checkpointer, cacheDataAdapter, EventHubDataComparer.Instance)
        {
        }

        protected override string GetOffset(CachedEventHubMessage lastItemPurged)
        {
            int readOffset = 0;
            SegmentBuilder.ReadNextString(lastItemPurged.Segment, ref readOffset); // read namespace, not needed so throw away.
            return SegmentBuilder.ReadNextString(lastItemPurged.Segment, ref readOffset); // read offset
        }

        protected override bool TryCalculateCachePressureContribution(StreamSequenceToken token, out double cachePressureContribution)
        {
            cachePressureContribution = 0;
            // if cache is empty or has few items, don't calculate pressure
            if (cache.IsEmpty ||
                !cache.Newest.HasValue ||
                !cache.Oldest.HasValue ||
                cache.Newest.Value.SequenceNumber - cache.Oldest.Value.SequenceNumber < 10*defaultMaxAddCount) // not enough items in cache.
            {
                return false;
            }

            IEventHubPartitionLocation location = (IEventHubPartitionLocation) token;
            double cacheSize = cache.Newest.Value.SequenceNumber - cache.Oldest.Value.SequenceNumber;
            long distanceFromNewestMessage = cache.Newest.Value.SequenceNumber - location.SequenceNumber;

            // pressure is the ratio of the distance from the front of the cache to the 
            cachePressureContribution = distanceFromNewestMessage/cacheSize;

            return true;
        }
    }
}
