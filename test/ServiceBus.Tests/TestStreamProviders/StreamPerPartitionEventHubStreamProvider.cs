using System;
using System.Text;

#if NETSTANDARD
using Microsoft.Azure.EventHubs;
#else

using Microsoft.ServiceBus.Messaging;

#endif

using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;
using TestExtensions;
using Xunit;

namespace ServiceBus.Tests.TestStreamProviders.EventHub
{
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class StreamPerPartitionEventHubStreamProvider : PersistentStreamProvider<StreamPerPartitionEventHubStreamProvider.AdapterFactory>
    {
        private class CacheFactory : IEventHubQueueCacheFactory
        {
            private readonly EventHubStreamProviderSettings adapterSettings;
            private readonly SerializationManager serializationManager;
            private readonly TimePurgePredicate timePurgePredicate;

            public CacheFactory(EventHubStreamProviderSettings adapterSettings, SerializationManager serializationManager)
            {
                this.adapterSettings = adapterSettings;
                this.serializationManager = serializationManager;
                timePurgePredicate = new TimePurgePredicate(adapterSettings.DataMinTimeInCache, adapterSettings.DataMaxAgeInCache);
            }

            public IEventHubQueueCache CreateCache(string partition, IStreamQueueCheckpointer<string> checkpointer, Logger cacheLogger)
            {
                var bufferPool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(1 << 20), null, null);
                var dataAdapter = new CachedDataAdapter(partition, bufferPool, this.serializationManager);
                return new EventHubQueueCache(checkpointer, dataAdapter, EventHubDataComparer.Instance, cacheLogger, 
                    new EventHubCacheEvictionStrategy(cacheLogger, this.timePurgePredicate, null, null), null, null);
            }
        }

        public class AdapterFactory : EventHubAdapterFactory
        {
            protected override IEventHubQueueCacheFactory CreateCacheFactory(EventHubStreamProviderSettings providerSettings)
            {
                return new CacheFactory(providerSettings, SerializationManager);
            }
        }

        private class CachedDataAdapter : EventHubDataAdapter
        {
            private readonly Guid partitionStreamGuid;

            public CachedDataAdapter(string partitionKey, IObjectPool<FixedSizeBuffer> bufferPool, SerializationManager serializationManager)
                : base(serializationManager, bufferPool)
            {
                partitionStreamGuid = GetPartitionGuid(partitionKey);
            }

            public override StreamPosition GetStreamPosition(EventData queueMessage)
            {
                IStreamIdentity stremIdentity = new StreamIdentity(partitionStreamGuid, null);
                StreamSequenceToken token =
#if NETSTANDARD
                new EventHubSequenceTokenV2(queueMessage.SystemProperties.Offset, queueMessage.SystemProperties.SequenceNumber, 0);
#else
                new EventHubSequenceTokenV2(queueMessage.Offset, queueMessage.SequenceNumber, 0);
#endif
                return new StreamPosition(stremIdentity, token);
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