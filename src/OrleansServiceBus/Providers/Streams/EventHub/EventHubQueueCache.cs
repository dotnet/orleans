
using System;
using Microsoft.ServiceBus.Messaging;
using Orleans.Providers.Streams.Common;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;

namespace OrleansServiceBus.Providers.Streams.EventHub
{
    public abstract class EventHubQueueCache<TCachedMessage> : PooledQueueCache<EventData, TCachedMessage>, IEventHubQueueCache
        where TCachedMessage : struct
    {
        protected IStreamQueueCheckpointer<string> Checkpointer { private set; get; }

        protected EventHubQueueCache(IStreamQueueCheckpointer<string> checkpointer, ICacheDataAdapter<EventData, TCachedMessage> cacheDataAdapter, ICacheDataComparer<TCachedMessage> comparer)
            : base(cacheDataAdapter, comparer)
        {
            cacheDataAdapter.PurgeAction = Purge;
            Checkpointer = checkpointer;
            OnPurged = CheckpointOnPurged;
        }

        public void Dispose()
        {
            OnPurged = null;
        }

        protected abstract string GetOffset(TCachedMessage lastItemPurged);

        private void CheckpointOnPurged(TCachedMessage lastItemPurged)
        {
            Checkpointer.Update(GetOffset(lastItemPurged), DateTime.UtcNow);
        }
    }

    /// <summary>
    /// Message cache that stores EventData as a CachedEventHubMessage in a pooled message cache
    /// </summary>
    internal class DefaultEventHubQueueCache : EventHubQueueCache<CachedEventHubMessage>
    {
        public DefaultEventHubQueueCache(IStreamQueueCheckpointer<string> checkpointer, IObjectPool<FixedSizeBuffer> bufferPool)
            : base(checkpointer, new EventHubDataAdapter(bufferPool), EventHubDataComparer.Instance)
        {
        }

        protected override string GetOffset(CachedEventHubMessage lastItemPurged)
        {
            int readOffset = 0;
            SegmentBuilder.ReadNextString(lastItemPurged.Segment, ref readOffset); // read namespace, not needed so throw away.
            return SegmentBuilder.ReadNextString(lastItemPurged.Segment, ref readOffset); // read offset
        }
    }
}
