
using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.ServiceBus.Messaging;
using Orleans.Providers.Streams.Common;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;

namespace Tester.TestStreamProviders.EventHub
{
    public class StreamPerPartitionEventHubStreamProvider : PersistentStreamProvider<StreamPerPartitionEventHubStreamProvider.AdapterFactory>
    {
        public class AdapterFactory : EventHubAdapterFactory
        {
            public AdapterFactory()
            {
                CacheFactory = CreateQueueCache;
            }

            private IEventHubQueueCache CreateQueueCache(string partition, IStreamQueueCheckpointer<string> checkpointer)
            {
                var bufferPool = new FixedSizeObjectPool<FixedSizeBuffer>(adapterConfig.CacheSizeMb, pool => new FixedSizeBuffer(1 << 20, pool));
                return new QueueCache(partition, checkpointer, bufferPool);
            }

        }

        /// <summary>
        /// Message cache that stores EventData as a CachedEventHubMessage in a pooled message cache
        /// </summary>
        private class QueueCache : EventHubQueueCache
        {
            public QueueCache(string partition, IStreamQueueCheckpointer<string> checkpointer, IObjectPool<FixedSizeBuffer> bufferPool)
                : base(checkpointer, new CachedDataAdapter(partition, bufferPool))
            {
            }

            private class CachedDataAdapter : EventHubDataAdapter
            {
                private readonly Guid partitionStreamGuid;

                public CachedDataAdapter(string partitionKey, IObjectPool<FixedSizeBuffer> bufferPool)
                    : base(bufferPool)
                {
                    partitionStreamGuid = GetPartitionGuid(partitionKey);
                }


                public override StreamPosition GetStreamPosition(EventData queueMessage)
                {
                    IStreamIdentity stremIdentity = new StreamIdentity(partitionStreamGuid, null);
                    StreamSequenceToken token = new EventSequenceToken(queueMessage.SequenceNumber, 0);
                    return new StreamPosition(stremIdentity, token);
                }
            }
        }

        public static Guid GetPartitionGuid(string partition)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(partition);
            Array.Resize(ref bytes, 10);
            return new Guid(partition.GetHashCode(), bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5], bytes[6], bytes[7], bytes[8], bytes[9]);
        }
    }
}
