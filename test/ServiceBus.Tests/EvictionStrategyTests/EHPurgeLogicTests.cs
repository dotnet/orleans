using Microsoft.ServiceBus.Messaging;
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
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TestExtensions;
using Xunit;

namespace ServiceBus.Tests.EventHubCacheEvictionStrategyTests
{
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
            var config = new ClientConfiguration();
            var environment = SerializationTestEnvironment.InitializeWithDefaults(config);
            this.serializationManager = environment.SerializationManager;

            //set up buffer pool
            this.bufferPoolSizeInMB = EventHubStreamProviderSettings.DefaultCacheSizeMb;
            this.bufferPool = new FixedSizeObjectPool<FixedSizeBuffer>(this.bufferPoolSizeInMB, () => new FixedSizeBuffer(1 << 20));

            //set up logger
            this.logger = new NoOpTestLogger().GetLogger(this.GetType().Name);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming"), TestCategory("BVT")]
        public async Task EventhubQueueCache_WontPurge_WhenUnderPressure()
        {
            InitForTesting();
            var tasks = new List<Task>();
            //add items into cache
            int itemAddToCache = 10;
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

        [Fact, TestCategory("EventHub"), TestCategory("Streaming"), TestCategory("BVT")]
        public async Task EventhubQueueCache_WontPurge_WhenTimePurgePredicateSaysDontPurge()
        {
            InitForTesting();
            var tasks = new List<Task>();
            //add items into cache
            int itemAddToCache = 10;
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

        [Fact, TestCategory("EventHub"), TestCategory("Streaming"), TestCategory("BVT")]
        public async Task EventhubQueueCache_WillPurge_WhenTimePurgePredicateSaysPurge_And_NotUnderPressure()
        {
            InitForTesting();
            var tasks = new List<Task>();
            //add items into cache
            int itemAddToCache = 10;
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
            int expectedItemCountInCacheList = 0;
            Assert.Equal(expectedItemCountInCacheList, GetItemCountInAllCache(this.cacheList));
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming"), TestCategory("BVT")]
        public async Task EventhubQueueCache_BufferPoolFull_WontCauseCacheMiss()
        {
            InitForTesting();
            var tasks = new List<Task>();
            //add items into cache
            int itemAddToCache = 10;
            foreach (var cache in this.cacheList)
                tasks.Add(AddDataIntoCache(cache, itemAddToCache));
            await Task.WhenAll(tasks);

            //set cachePressureMonitor to be underPressure
            this.cachePressureInjectionMonitor.isUnderPressure = false;
            //set purgePredicate to be ShouldPurge
            this.purgePredicate.ShouldPurge = false;

            //keep allocate buffer on buffer pool to make it full
            int bufferToAllocate = EventHubStreamProviderSettings.DefaultCacheSizeMb;
            while (bufferToAllocate > 0)
            {
                this.bufferPool.Allocate();
                bufferToAllocate--;
            }

            //Assert
            int expectedItemCountInCacheList = itemAddToCache + itemAddToCache;
            Assert.Equal(expectedItemCountInCacheList, GetItemCountInAllCache(this.cacheList));
        }

        private void InitForTesting()
        {
            this.cacheList = new ConcurrentBag<EventHubQueueCacheForTesting>();
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
                cache.Add(new EventData(), DateTime.UtcNow);
            }
            return TaskDone.Done;
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
            var cache = new EventHubQueueCacheForTesting(checkpointer, new MockEventHubCacheAdaptor(this.serializationManager, this.bufferPool), 
                EventHubDataComparer.Instance, this.logger, new EventHubCacheEvictionStrategy(this.logger, this.purgePredicate));
            cache.AddCachePressureMonitor(this.cachePressureInjectionMonitor);
            this.cacheList.Add(cache);
            return cache;
        }
    }
}
