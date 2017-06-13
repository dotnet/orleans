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
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;

namespace ServiceBus.Tests.TestStreamProviders.EventHub
{
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class StreamPerPartitionEventHubStreamProvider : PersistentStreamProvider<StreamPerPartitionEventHubStreamProvider.AdapterFactory>
    {
        private class CacheFactory : EventHubQueueCacheFactory
        { 

            public CacheFactory(EventHubStreamProviderSettings providerSettings,
            SerializationManager serializationManager, EventHubMonitorAggregationDimensions sharedDimensions,
            Func<EventHubCacheMonitorDimensions, Logger, ICacheMonitor> cacheMonitorFactory = null,
            Func<EventHubBlockPoolMonitorDimensions, Logger, IBlockPoolMonitor> blockPoolMonitorFactory = null)
                :base(providerSettings, serializationManager, sharedDimensions, cacheMonitorFactory, blockPoolMonitorFactory)
            {
            }

            protected override IObjectPool<FixedSizeBuffer> CreateBufferPool(EventHubStreamProviderSettings providerSettings, Logger logger, EventHubMonitorAggregationDimensions sharedDimensions, out string blockPoolId)
            {
                var bufferSize = 1 << 20;
                var bufferPoolId = $"BlockPool-{new Guid().ToString()}-BlockSize-{bufferSize}";
                var monitorDimensions = new EventHubBlockPoolMonitorDimensions(sharedDimensions, bufferPoolId);
                var objectPoolMonitor = new ObjectPoolMonitorBridge(this.BlockPoolMonitorFactory(monitorDimensions, logger), bufferSize);
                var bufferPool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(bufferSize),
                    objectPoolMonitor, providerSettings.StatisticMonitorWriteInterval);
                blockPoolId = bufferPoolId;
                return bufferPool;
            }
        }

        public class AdapterFactory : EventHubAdapterFactory
        {
            protected override IEventHubQueueCacheFactory CreateCacheFactory(EventHubStreamProviderSettings providerSettings)
            {
                var globalConfig = this.serviceProvider.GetRequiredService<GlobalConfiguration>();
                var nodeConfig = this.serviceProvider.GetRequiredService<NodeConfiguration>();
                var eventHubPath = hubSettings.Path;
                var sharedDimensions = new EventHubMonitorAggregationDimensions(globalConfig, nodeConfig, eventHubPath);
                return new CacheFactory(providerSettings, SerializationManager, sharedDimensions);
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