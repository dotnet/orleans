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
        private FixedSizeObjectPool<FixedSizeBuffer> bufferPool;
        private int bufferPoolSizeInMB;
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
            this.bufferPoolSizeInMB = EventHubStreamProviderSettings.DefaultCacheSizeMb;
            var oneKB = 1024;
            this.bufferPool = new FixedSizeObjectPool<FixedSizeBuffer>(this.bufferPoolSizeInMB, () => new FixedSizeBuffer(oneKB));

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
        public async Task EventhubQueueCache_BufferPoolFull_WontCauseCacheMiss()
        {
            InitForTesting();
            var tasks = new List<Task>();
            //add items into cache
            int itemAddToCache = 100;
            foreach (var cache in this.cacheList)
                tasks.Add(AddDataIntoCache(cache, itemAddToCache));
            await Task.WhenAll(tasks);

            //set up condition so that purge shouldn't be performed
            this.cachePressureInjectionMonitor.isUnderPressure = false;
            this.purgePredicate.ShouldPurge = false;

            //keep allocate buffer on buffer pool to make it full
            int bufferToAllocate = EventHubStreamProviderSettings.DefaultCacheSizeMb;
            while (bufferToAllocate > 0)
            {
                this.bufferPool.Allocate();
                bufferToAllocate--;
            }

            //Assert, no item got purged
            int expectedItemCountInCacheList = itemAddToCache + itemAddToCache;
            Assert.Equal(expectedItemCountInCacheList, GetItemCountInAllCache(this.cacheList));
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
            this.evictionStrategyList.ForEach(strategy => Assert.Equal(0, strategy.PurgedBuffers.Count));

            //perform purge
            IList<IBatchContainer> ignore;
            this.receiver1.TryPurgeFromCache(out ignore);
            this.receiver2.TryPurgeFromCache(out ignore);

            //Each cache should each have buffers purged, while current buffer stay in inUseBuffers
            this.evictionStrategyList.ForEach(strategy => Assert.Equal(1, strategy.InUseBuffers.Count));
            this.evictionStrategyList.ForEach(strategy => Assert.True(strategy.PurgedBuffers.Count > 0));

            var purgedBuffers = new List<FixedSizeBuffer>();
            this.evictionStrategyList.ForEach(strategy =>
            {
                var purgedBufferList = strategy.PurgedBuffers.ToArray<FixedSizeBuffer>();
                foreach(var purgedBuffer in purgedBufferList)
                    purgedBuffers.Add(purgedBuffer);
            });

            var newBuffersAllocated = new List<FixedSizeBuffer>();
            //keep allocate buffer on buffer pool to make it full, so that buffer pool will request purged buffers to return
            int bufferToAllocate = EventHubStreamProviderSettings.DefaultCacheSizeMb;
            while (bufferToAllocate > 0)
            {
                newBuffersAllocated.Add(this.bufferPool.Allocate());
                bufferToAllocate--;
            }

            //Purged buffers should be returned to the pool and used to allocate new buffer
            purgedBuffers.ForEach(buffer => Assert.True(newBuffersAllocated.Contains(buffer)));
            this.evictionStrategyList.ForEach(strategy => Assert.Equal(1, strategy.InUseBuffers.Count));
            this.evictionStrategyList.ForEach(strategy => Assert.Equal(0, strategy.PurgedBuffers.Count));
        }
#endif

        private void InitForTesting()
        {
            this.cacheList = new ConcurrentBag<EventHubQueueCacheForTesting>();
            this.evictionStrategyList = new List<EHEvictionStrategyForTesting>();
            this.receiver1 = new EventHubAdapterReceiver(ehSettings, this.CacheFactory, this.CheckPointerFactory, this.logger,
                new DefaultEventHubReceiverMonitor(ehSettings.Hub.Path, ehSettings.Partition, this.logger), this.GetNodeConfiguration);
            this.receiver2 = new EventHubAdapterReceiver(ehSettings, this.CacheFactory, this.CheckPointerFactory, this.logger,
                new DefaultEventHubReceiverMonitor(ehSettings.Hub.Path, ehSettings.Partition, this.logger), this.GetNodeConfiguration);
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
            return Task.FromResult<IStreamQueueCheckpointer<string>>(new MockStreamQueueCheckpointer());
        }

        private IEventHubQueueCache CacheFactory(string partition, IStreamQueueCheckpointer<string> checkpointer, Logger logger)
        {
            var evictionStrategy = new EHEvictionStrategyForTesting(this.logger, this.purgePredicate);
            this.evictionStrategyList.Add(evictionStrategy);
            var cache = new EventHubQueueCacheForTesting(checkpointer, new MockEventHubCacheAdaptor(this.serializationManager, this.bufferPool), 
                EventHubDataComparer.Instance, this.logger, evictionStrategy);
            cache.AddCachePressureMonitor(this.cachePressureInjectionMonitor);
            this.cacheList.Add(cache);
            return cache;
        }
    }
}
