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
#if NETSTANDARD
using Microsoft.Azure.EventHubs;
#else
using Microsoft.ServiceBus.Messaging;
#endif
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
        }
        //Disable tests if in netstandard, because Eventhub framework doesn't provide proper hooks for tests to generate proper EventData in netstandard
#if !NETSTANDARD
        [Fact, TestCategory("BVT")]
        public async Task EventhubQueueCache_WontPurge_WhenUnderPressure()
        {
            InitForTesting();
            var tasks = new List<Task>();
            //add items into cache, make sure will allocate multiple buffers from the pool
            int itemAddToCache = 100;
            foreach(var cache in this.cacheList)
                tasks.Add(AddDataIntoCache(cache, itemAddToCache));
            await Task.WhenAll(tasks);

            //set cachePressureMonitor to be underPressure
            this.cachePressureInjectionMonitor.isUnderPressure = true;
            //set purgePredicate to be ShouldPurge
            this.purgePredicate.ShouldPurge = true;

            //perform purge
            IList<IBatchContainer> ignore; 
            this.receiver1.TryPurgeFromCache(out ignore);
            this.receiver2.TryPurgeFromCache(out ignore);

            //Assert
            int expectedItemCountInCacheList = itemAddToCache + itemAddToCache;
            Assert.Equal(expectedItemCountInCacheList, GetItemCountInAllCache(this.cacheList));
        }

        [Fact, TestCategory("BVT")]
        public async Task EventhubQueueCache_WontPurge_WhenTimePurgePredicateSaysDontPurge()
        {
            InitForTesting();
            var tasks = new List<Task>();
            //add items into cache
            int itemAddToCache = 100;
            foreach (var cache in this.cacheList)
                tasks.Add(AddDataIntoCache(cache, itemAddToCache));
            await Task.WhenAll(tasks);

            //set cachePressureMonitor to be underPressure
            this.cachePressureInjectionMonitor.isUnderPressure = false;
            //set purgePredicate to be ShouldPurge
            this.purgePredicate.ShouldPurge = false;

            //perform purge
            IList<IBatchContainer> ignore;
            this.receiver1.TryPurgeFromCache(out ignore);
            this.receiver2.TryPurgeFromCache(out ignore);

            //Assert
            int expectedItemCountInCacheList = itemAddToCache + itemAddToCache;
            Assert.Equal(expectedItemCountInCacheList, GetItemCountInAllCache(this.cacheList));
        }

        [Fact, TestCategory("BVT")]
        public async Task EventhubQueueCache_WillPurge_WhenTimePurgePredicateSaysPurge_And_NotUnderPressure()
        {
            InitForTesting();
            var tasks = new List<Task>();
            //add items into cache
            int itemAddToCache = 100;
            foreach (var cache in this.cacheList)
                tasks.Add(AddDataIntoCache(cache, itemAddToCache));
            await Task.WhenAll(tasks);

            //set cachePressureMonitor to be underPressure
            this.cachePressureInjectionMonitor.isUnderPressure = false;
            //set purgePredicate to be ShouldPurge
            this.purgePredicate.ShouldPurge = true;

            //perform purge
            IList<IBatchContainer> ignore;
            this.receiver1.TryPurgeFromCache(out ignore);
            this.receiver2.TryPurgeFromCache(out ignore);

            //Assert
            int expectedItemCountInCaches = 0;
            //items got purged
            Assert.Equal(expectedItemCountInCaches, GetItemCountInAllCache(this.cacheList));
        }

        [Fact, TestCategory("BVT")]
        public async Task EventhubQueueCache_EvictionStrategy_Behavior()
        {
            InitForTesting();
            var tasks = new List<Task>();
            //add items into cache
            int itemAddToCache = 100;
            foreach (var cache in this.cacheList)
                tasks.Add(AddDataIntoCache(cache, itemAddToCache));
            await Task.WhenAll(tasks);

            //set up condition so that purge will be performed
            this.cachePressureInjectionMonitor.isUnderPressure = false;
            this.purgePredicate.ShouldPurge = true;

            //Each cache should each have buffers allocated
            this.evictionStrategyList.ForEach(strategy => Assert.True(strategy.InUseBuffers.Count > 0));

            //perform purge

            //after purge, inUseBuffers should be purged and return to the pool, except for the current buffer
            var expectedPurgedBuffers = new List<FixedSizeBuffer>();
            this.evictionStrategyList.ForEach(strategy =>
            {
                var purgedBufferList = strategy.InUseBuffers.ToArray<FixedSizeBuffer>();
                //last one in purgedBufferList should be current buffer, which shouldn't be purged
                for (int i = 0; i < purgedBufferList.Count() - 1; i++)
                    expectedPurgedBuffers.Add(purgedBufferList[i]);
            });

            IList<IBatchContainer> ignore;
            this.receiver1.TryPurgeFromCache(out ignore);
            this.receiver2.TryPurgeFromCache(out ignore);

            //Each cache should have all buffers purged, except for current buffer
            this.evictionStrategyList.ForEach(strategy => Assert.Equal(1, strategy.InUseBuffers.Count));
            var oldBuffersInCaches = new List<FixedSizeBuffer>();
            this.evictionStrategyList.ForEach(strategy => {
                foreach (var inUseBuffer in strategy.InUseBuffers)
                    oldBuffersInCaches.Add(inUseBuffer);
                });
            //add items into cache again
            itemAddToCache = 100;
            foreach (var cache in this.cacheList)
                tasks.Add(AddDataIntoCache(cache, itemAddToCache));
            await Task.WhenAll(tasks);
            //block pool should have purged buffers returned by now, and used those to allocate buffer for new item
            var newBufferAllocated = new List<FixedSizeBuffer>();
            this.evictionStrategyList.ForEach(strategy => {
                foreach (var inUseBuffer in strategy.InUseBuffers)
                    newBufferAllocated.Add(inUseBuffer);
            });
            //remove old buffer in cache, to get newly allocated buffers after purge
            newBufferAllocated.RemoveAll(buffer => oldBuffersInCaches.Contains(buffer));
            //purged buffer should return to the pool after purge, and used to allocate new buffer
            expectedPurgedBuffers.ForEach(buffer => Assert.True(newBufferAllocated.Contains(buffer)));
        }
#endif

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
                new DefaultEventHubReceiverMonitor(monitorDimensions, this.logger), this.GetNodeConfiguration);
            this.receiver2 = new EventHubAdapterReceiver(ehSettings, this.CacheFactory, this.CheckPointerFactory, this.logger,
                new DefaultEventHubReceiverMonitor(monitorDimensions, this.logger), this.GetNodeConfiguration);
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

        private IEventHubQueueCache CacheFactory(string partition, IStreamQueueCheckpointer<string> checkpointer, Logger logger)
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
