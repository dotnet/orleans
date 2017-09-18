using Orleans;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;
using Orleans.TestingHost.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Azure.EventHubs;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TestExtensions;
using Xunit;
using Orleans.ServiceBus.Providers.Testing;

namespace ServiceBus.Tests.EvictionStrategyTests
{
    [TestCategory("EventHub"), TestCategory("Streaming")]
    public class EHPurgeLogicTests
    {
        private CachePressureInjectionMonitor cachePressureInjectionMonitor;
        private PurgeDecisionInjectionPredicate purgePredicate;
        private SerializationManager serializationManager;
        private EventHubAdapterReceiver receiver1;
        private EventHubAdapterReceiver receiver2;
        private ObjectPool<FixedSizeBuffer> bufferPool;
        private Logger logger;
        private TimeSpan timeOut = TimeSpan.FromSeconds(30);
        private EventHubPartitionSettings ehSettings;
        private ConcurrentBag<EventHubQueueCacheForTesting> cacheList;
        private List<EHEvictionStrategyForTesting> evictionStrategyList;
        private ITelemetryProducer telemetryProducer;

        public EHPurgeLogicTests()
        {
            //an mock eh settings
            this.ehSettings = new EventHubPartitionSettings();
            ehSettings.Hub = new EventHubSettings();
            ehSettings.Partition = "MockPartition";

            //set up cache pressure monitor and purge predicate
            this.cachePressureInjectionMonitor = new CachePressureInjectionMonitor();
            this.purgePredicate = new PurgeDecisionInjectionPredicate(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30));

            //set up serialization env
            var environment = SerializationTestEnvironment.InitializeWithDefaults();
            this.serializationManager = environment.SerializationManager;

            //set up buffer pool, small buffer size make it easy for cache to allocate multiple buffers
            var oneKB = 1024;
            this.bufferPool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(oneKB));

            //set up logger
            this.logger = new NoOpTestLogger().GetLogger(this.GetType().Name);
            this.telemetryProducer = new NullTelemetryProducer();
        }

        private void InitForTesting()
        {
            this.cacheList = new ConcurrentBag<EventHubQueueCacheForTesting>();
            this.evictionStrategyList = new List<EHEvictionStrategyForTesting>();
            var monitorDimensions = new EventHubReceiverMonitorDimensions();
            monitorDimensions.EventHubPartition = ehSettings.Partition;
            monitorDimensions.EventHubPath = ehSettings.Hub.Path;
            monitorDimensions.GlobalConfig = null;
            monitorDimensions.NodeConfig = null;

            this.receiver1 = new EventHubAdapterReceiver(ehSettings, this.CacheFactory, this.CheckPointerFactory, this.logger,
                new DefaultEventHubReceiverMonitor(monitorDimensions, this.telemetryProducer), this.GetNodeConfiguration, this.telemetryProducer);
            this.receiver2 = new EventHubAdapterReceiver(ehSettings, this.CacheFactory, this.CheckPointerFactory, this.logger,
                new DefaultEventHubReceiverMonitor(monitorDimensions, this.telemetryProducer), this.GetNodeConfiguration, this.telemetryProducer);
            this.receiver1.Initialize(this.timeOut);
            this.receiver2.Initialize(this.timeOut);
        }

        private int GetItemCountInAllCache(ConcurrentBag<EventHubQueueCacheForTesting> caches)
        {
            int itemCount = 0;
            foreach (var cache in caches)
            {
                itemCount += cache.ItemCount;
            }
            return itemCount;
        }
        private Task AddDataIntoCache(EventHubQueueCacheForTesting cache, int count)
        {
            while (count > 0)
            {
                count--;
                //just to make compiler happy
                byte[] ignore = { 12, 23 };
                cache.Add(new EventData(ignore), DateTime.UtcNow);
            }
            return Task.CompletedTask;
        }

        private NodeConfiguration GetNodeConfiguration()
        {
            return new NodeConfiguration();
        }

        private Task<IStreamQueueCheckpointer<string>> CheckPointerFactory(string partition)
        {
            return Task.FromResult<IStreamQueueCheckpointer<string>>(NoOpCheckpointer.Instance);
        }

        private IEventHubQueueCache CacheFactory(string partition, IStreamQueueCheckpointer<string> checkpointer, Logger logger, ITelemetryProducer telemetryProducer)
        {
            var evictionStrategy = new EHEvictionStrategyForTesting(this.logger, null, null, this.purgePredicate);
            this.evictionStrategyList.Add(evictionStrategy);
            var cache = new EventHubQueueCacheForTesting(checkpointer, new MockEventHubCacheAdaptor(this.serializationManager, this.bufferPool),
                EventHubDataComparer.Instance, this.logger, evictionStrategy);
            cache.AddCachePressureMonitor(this.cachePressureInjectionMonitor);
            this.cacheList.Add(cache);
            return cache;
        }
    }
}
