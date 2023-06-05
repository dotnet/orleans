using Orleans.Providers.Streams.Common;
using Orleans.Streaming.EventHubs;
using Orleans.Streams;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Xunit;
using Orleans.Streaming.EventHubs.Testing;
using Azure.Messaging.EventHubs;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Statistics;

namespace ServiceBus.Tests.EvictionStrategyTests
{
    [TestCategory("EventHub"), TestCategory("Streaming")]
    public class EHPurgeLogicTests
    {
        private CachePressureInjectionMonitor cachePressureInjectionMonitor;
        private PurgeDecisionInjectionPredicate purgePredicate;
        private Serializer serializer;
        private EventHubAdapterReceiver receiver1;
        private EventHubAdapterReceiver receiver2;
        private ObjectPool<FixedSizeBuffer> bufferPool;
        private TimeSpan timeOut = TimeSpan.FromSeconds(30);
        private EventHubPartitionSettings ehSettings;
        private NoOpHostEnvironmentStatistics _hostEnvironmentStatistics;
        private ConcurrentBag<EventHubQueueCacheForTesting> cacheList;
        private List<EHEvictionStrategyForTesting> evictionStrategyList;

        public EHPurgeLogicTests()
        {
            //an mock eh settings
            ehSettings = new EventHubPartitionSettings
            {
                Hub = new EventHubOptions(),
                Partition = "MockPartition",
                ReceiverOptions = new EventHubReceiverOptions()
            };

            //set up cache pressure monitor and purge predicate
            cachePressureInjectionMonitor = new CachePressureInjectionMonitor();
            purgePredicate = new PurgeDecisionInjectionPredicate(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30));

            // set up serialization env
            var serviceProvider = new ServiceCollection()
                .AddSerializer()
                .BuildServiceProvider();
            serializer = serviceProvider.GetRequiredService<Serializer>();

            //set up buffer pool, small buffer size make it easy for cache to allocate multiple buffers
            var oneKB = 1024;
            bufferPool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(oneKB));
        }

        [Fact, TestCategory("BVT")]
        public async Task EventhubQueueCache_WontPurge_WhenUnderPressure()
        {
            InitForTesting();
            var tasks = new List<Task>();
            //add items into cache, make sure will allocate multiple buffers from the pool
            var itemAddToCache = 100;
            foreach(var cache in cacheList)
                tasks.Add(AddDataIntoCache(cache, itemAddToCache));
            await Task.WhenAll(tasks);

            //set cachePressureMonitor to be underPressure
            cachePressureInjectionMonitor.isUnderPressure = true;
            //set purgePredicate to be ShouldPurge
            purgePredicate.ShouldPurge = true;
            receiver1.TryPurgeFromCache(out _);
            receiver2.TryPurgeFromCache(out _);

            //Assert
            var expectedItemCountInCacheList = itemAddToCache + itemAddToCache;
            Assert.Equal(expectedItemCountInCacheList, GetItemCountInAllCache(cacheList));
        }

        [Fact, TestCategory("BVT")]
        public async Task EventhubQueueCache_WontPurge_WhenTimePurgePredicateSaysDontPurge()
        {
            InitForTesting();
            var tasks = new List<Task>();
            //add items into cache
            var itemAddToCache = 100;
            foreach (var cache in cacheList)
                tasks.Add(AddDataIntoCache(cache, itemAddToCache));
            await Task.WhenAll(tasks);

            //set cachePressureMonitor to be underPressure
            cachePressureInjectionMonitor.isUnderPressure = false;
            //set purgePredicate to be ShouldPurge
            purgePredicate.ShouldPurge = false;

            //perform purge
            receiver1.TryPurgeFromCache(out _);
            receiver2.TryPurgeFromCache(out _);

            //Assert
            var expectedItemCountInCacheList = itemAddToCache + itemAddToCache;
            Assert.Equal(expectedItemCountInCacheList, GetItemCountInAllCache(cacheList));
        }

        [Fact, TestCategory("BVT")]
        public async Task EventhubQueueCache_WillPurge_WhenTimePurgePredicateSaysPurge_And_NotUnderPressure()
        {
            InitForTesting();
            var tasks = new List<Task>();
            //add items into cache
            var itemAddToCache = 100;
            foreach (var cache in cacheList)
                tasks.Add(AddDataIntoCache(cache, itemAddToCache));
            await Task.WhenAll(tasks);

            //set cachePressureMonitor to be underPressure
            cachePressureInjectionMonitor.isUnderPressure = false;
            //set purgePredicate to be ShouldPurge
            purgePredicate.ShouldPurge = true;

            //perform purge
            receiver1.TryPurgeFromCache(out _);
            receiver2.TryPurgeFromCache(out _);

            //Assert
            var expectedItemCountInCaches = 0;
            //items got purged
            Assert.Equal(expectedItemCountInCaches, GetItemCountInAllCache(cacheList));
        }

        [Fact, TestCategory("BVT")]
        public async Task EventhubQueueCache_EvictionStrategy_Behavior()
        {
            InitForTesting();
            var tasks = new List<Task>();
            //add items into cache
            var itemAddToCache = 100;
            foreach (var cache in cacheList)
                tasks.Add(AddDataIntoCache(cache, itemAddToCache));
            await Task.WhenAll(tasks);

            //set up condition so that purge will be performed
            cachePressureInjectionMonitor.isUnderPressure = false;
            purgePredicate.ShouldPurge = true;

            //Each cache should each have buffers allocated
            evictionStrategyList.ForEach(strategy => Assert.True(strategy.InUseBuffers.Count > 0));

            //perform purge

            //after purge, inUseBuffers should be purged and return to the pool, except for the current buffer
            var expectedPurgedBuffers = new List<FixedSizeBuffer>();
            evictionStrategyList.ForEach(strategy =>
            {
                var purgedBufferList = strategy.InUseBuffers.ToArray<FixedSizeBuffer>();
                //last one in purgedBufferList should be current buffer, which shouldn't be purged
                for (var i = 0; i < purgedBufferList.Count() - 1; i++)
                    expectedPurgedBuffers.Add(purgedBufferList[i]);
            });

            IList<IBatchContainer> ignore;
            receiver1.TryPurgeFromCache(out ignore);
            receiver2.TryPurgeFromCache(out ignore);

            //Each cache should have all buffers purged, except for current buffer
            evictionStrategyList.ForEach(strategy => Assert.Single(strategy.InUseBuffers));
            var oldBuffersInCaches = new List<FixedSizeBuffer>();
            evictionStrategyList.ForEach(strategy => {
                foreach (var inUseBuffer in strategy.InUseBuffers)
                    oldBuffersInCaches.Add(inUseBuffer);
                });
            //add items into cache again
            itemAddToCache = 100;
            foreach (var cache in cacheList)
                tasks.Add(AddDataIntoCache(cache, itemAddToCache));
            await Task.WhenAll(tasks);
            //block pool should have purged buffers returned by now, and used those to allocate buffer for new item
            var newBufferAllocated = new List<FixedSizeBuffer>();
            evictionStrategyList.ForEach(strategy => {
                foreach (var inUseBuffer in strategy.InUseBuffers)
                    newBufferAllocated.Add(inUseBuffer);
            });
            //remove old buffer in cache, to get newly allocated buffers after purge
            newBufferAllocated.RemoveAll(buffer => oldBuffersInCaches.Contains(buffer));
            //purged buffer should return to the pool after purge, and used to allocate new buffer
            expectedPurgedBuffers.ForEach(buffer => Assert.Contains(buffer, newBufferAllocated));
        }

        private void InitForTesting()
        {
            _hostEnvironmentStatistics = new NoOpHostEnvironmentStatistics();
            cacheList = new ConcurrentBag<EventHubQueueCacheForTesting>();
            evictionStrategyList = new List<EHEvictionStrategyForTesting>();
            var monitorDimensions = new EventHubReceiverMonitorDimensions
            {
                EventHubPartition = ehSettings.Partition,
                EventHubPath = ehSettings.Hub.EventHubName,
            };

            receiver1 = new EventHubAdapterReceiver(ehSettings, CacheFactory, CheckPointerFactory, NullLoggerFactory.Instance, 
                new DefaultEventHubReceiverMonitor(monitorDimensions), new LoadSheddingOptions(), _hostEnvironmentStatistics);
            receiver2 = new EventHubAdapterReceiver(ehSettings, CacheFactory, CheckPointerFactory, NullLoggerFactory.Instance,
                new DefaultEventHubReceiverMonitor(monitorDimensions), new LoadSheddingOptions(), _hostEnvironmentStatistics);
            receiver1.Initialize(timeOut);
            receiver2.Initialize(timeOut);
        }

        private int GetItemCountInAllCache(ConcurrentBag<EventHubQueueCacheForTesting> caches)
        {
            var itemCount = 0;
            foreach (var cache in caches)
            {
                itemCount += cache.ItemCount;
            }
            return itemCount;
        }

        private async Task AddDataIntoCache(EventHubQueueCacheForTesting cache, int count)
        {
            await Task.Delay(10);
            var messages = Enumerable.Range(0, count)
                .Select(i => MakeEventData(i))
                .ToList();
            cache.Add(messages, DateTime.UtcNow);
        }

        private EventData MakeEventData(long sequenceNumber)
        {
            byte[] ignore = { 12, 23 };
            var now = DateTime.UtcNow;
            var eventData = new TestEventData(ignore,
                offset: now.Ticks,
                sequenceNumber: sequenceNumber,
                enqueuedTime: now);
            return eventData;
        }

        private class TestEventData : EventData
        {
            public TestEventData(ReadOnlyMemory<byte> eventBody, IDictionary<string, object> properties = null, IReadOnlyDictionary<string, object> systemProperties = null, long sequenceNumber = long.MinValue, long offset = long.MinValue, DateTimeOffset enqueuedTime = default, string partitionKey = null) : base(eventBody, properties, systemProperties, sequenceNumber, offset, enqueuedTime, partitionKey)
            {
            }
        }

        private Task<IStreamQueueCheckpointer<string>> CheckPointerFactory(string partition)
        {
            return Task.FromResult<IStreamQueueCheckpointer<string>>(NoOpCheckpointer.Instance);
        }

        private IEventHubQueueCache CacheFactory(string partition, IStreamQueueCheckpointer<string> checkpointer, ILoggerFactory loggerFactory)
        {
            var cacheLogger = loggerFactory.CreateLogger($"{typeof(EventHubQueueCacheForTesting)}.{partition}");
            var evictionStrategy = new EHEvictionStrategyForTesting(cacheLogger, null, null, purgePredicate);
            evictionStrategyList.Add(evictionStrategy);
            var cache = new EventHubQueueCacheForTesting(
                bufferPool,
                new MockEventHubCacheAdaptor(serializer),
                evictionStrategy,
                checkpointer,
                cacheLogger);
            cache.AddCachePressureMonitor(cachePressureInjectionMonitor);
            cacheList.Add(cache);
            return cache;
        }

        private class NoOpHostEnvironmentStatistics : IHostEnvironmentStatistics
        {
            public long? TotalPhysicalMemory => null;

            public float? CpuUsage => null;

            public long? AvailableMemory => null;
        }
    }
}
