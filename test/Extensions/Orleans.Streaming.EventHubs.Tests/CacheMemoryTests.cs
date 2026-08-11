using System.Globalization;
using Azure.Messaging.EventHubs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;
using Orleans.Streaming.EventHubs;
using Orleans.Streaming.EventHubs.Testing;
using Xunit;

namespace ServiceBus.Tests;

[TestSuite("BVT")]
[TestProvider("EventHub")]
[TestArea("Streaming")]
[TestCategory("EventHub"), TestCategory("Streaming")]
public sealed class CacheMemoryTests : IDisposable
{
    private readonly ServiceProvider serviceProvider;
    private readonly Serializer serializer;

    public CacheMemoryTests()
    {
        serviceProvider = new ServiceCollection()
            .AddMetrics()
            .AddSingleton<OrleansInstruments>()
            .AddSerializer()
            .BuildServiceProvider();
        serializer = serviceProvider.GetRequiredService<Serializer>();
    }

    [Fact, TestCategory("BVT")]
    public void CacheMemoryOptionsHaveBoundedDefaults()
    {
        var options = new EventHubStreamCacheMemoryOptions();

        Assert.Equal(512L * 1024 * 1024, options.MaxActiveCacheMemory);
        Assert.Equal(64L * 1024 * 1024, options.MaxBufferPoolMemory);
        new EventHubStreamCacheMemoryOptionsValidator(options, "test").ValidateConfiguration();
    }

    [Fact, TestCategory("BVT")]
    public void BufferPoolUsesSizeClassesAndBoundsIdleMemory()
    {
        var controller = new EventHubCacheMemoryController(1024 * 1024);
        var pool = new EventHubCacheBufferPool(controller, 64 * 1024, null, TimeSpan.FromMinutes(1));

        var small = pool.Allocate(1);
        var medium = pool.Allocate(100 * 1024);

        Assert.Equal(64 * 1024, small.SizeInByte);
        Assert.Equal(128 * 1024, medium.SizeInByte);
        Assert.Equal(192 * 1024, pool.ActiveMemory);
        Assert.Equal(pool.ActiveMemory, controller.ActiveCacheMemory);

        small.Dispose();
        medium.Dispose();

        Assert.Equal(0, pool.ActiveMemory);
        Assert.Equal(64 * 1024, pool.PooledMemory);

        var reused = pool.Allocate(1);
        Assert.Same(small, reused);
        reused.Dispose();
    }

    [Fact, TestCategory("BVT")]
    public void ProviderMemoryLimitAggregatesPartitionsAndRecoversAfterPurge()
    {
        const int maxActiveMemory = 450 * 1024;
        var controller = new EventHubCacheMemoryController(maxActiveMemory);
        var pool = new EventHubCacheBufferPool(controller, 64 * 1024, null, TimeSpan.FromMinutes(1));
        using var first = CreateCache("0", pool, controller);
        using var second = CreateCache("1", pool, controller);

        first.Add([MakeEventData(0, 80 * 1024), MakeEventData(1, 80 * 1024)], DateTime.UtcNow);
        Assert.True(first.GetMaxAddCount() > 0);

        // This simulates the batch which was already received when another partition crossed the watermark.
        second.Add([MakeEventData(2, 80 * 1024)], DateTime.UtcNow);
        Assert.True(controller.ActiveCacheMemory > maxActiveMemory);
        Assert.True(first.GetMaxAddCount() > 0);
        Assert.True(controller.ActiveCacheMemory < maxActiveMemory);
        Assert.True(controller.ActiveCacheMemory > 300 * 1024);
        Assert.True(second.GetMaxAddCount() > 0);
    }

    [Fact]
    public void SparseCachesRemainSmallAcross1024Partitions()
    {
        var options = new EventHubStreamCacheMemoryOptions();
        var controller = new EventHubCacheMemoryController(options.MaxActiveCacheMemory);
        var pool = new EventHubCacheBufferPool(controller, options.MaxBufferPoolMemory, null, TimeSpan.FromMinutes(1));
        var caches = new List<EventHubQueueCache>(1024);

        try
        {
            for (var i = 0; i < 1024; i++)
            {
                var cache = CreateCache(i.ToString(CultureInfo.InvariantCulture), pool, controller);
                cache.Add([MakeEventData(i)], DateTime.UtcNow);
                caches.Add(cache);
            }

            Assert.True(controller.ActiveCacheMemory < 128L * 1024 * 1024);
        }
        finally
        {
            foreach (var cache in caches)
            {
                cache.Dispose();
            }
        }
    }

    [Fact, TestCategory("BVT")]
    public void CustomBufferPoolPreservesLegacyMemoryBehavior()
    {
        var adapter = new TestEventHubDataAdapter(serializer);
        var factory = new CustomBufferPoolFactory(
            adapter,
            serviceProvider.GetRequiredService<OrleansInstruments>());
        using var cache = factory.CreateCache("0", NoOpCheckpointer.Instance, NullLoggerFactory.Instance);

        cache.Add([MakeEventData(0)], DateTime.UtcNow);

        Assert.True(cache.GetMaxAddCount() > 0);
    }

    [Fact, TestCategory("BVT")]
    public void CacheFactoryPublishesOneBufferPoolConcurrently()
    {
        var factory = new ExposedBufferPoolFactory(
            new TestEventHubDataAdapter(serializer),
            serviceProvider.GetRequiredService<OrleansInstruments>());
        var pools = new IObjectPool<FixedSizeBuffer>[64];

        Parallel.For(0, pools.Length, i => pools[i] = factory.GetBufferPool());

        Assert.All(pools, pool => Assert.Same(pools[0], pool));
    }

    [Fact, TestCategory("BVT")]
    public void DisposeClearsCacheBeforeDisposingEvictionStrategy()
    {
        var controller = new EventHubCacheMemoryController(1024 * 1024);
        var pool = new EventHubCacheBufferPool(controller, 64 * 1024, null, TimeSpan.FromMinutes(1));
        var evictionStrategy = new TrackingEvictionStrategy();
        var cache = CreateCache("0", pool, controller, evictionStrategy);
        cache.Add([MakeEventData(0)], DateTime.UtcNow);

        cache.Dispose();

        Assert.True(evictionStrategy.WasCacheEmptyOnDispose);
    }

    public void Dispose() => serviceProvider.Dispose();

    private EventHubQueueCache CreateCache(
        string partition,
        IObjectPool<FixedSizeBuffer> pool,
        EventHubCacheMemoryController controller,
        IEvictionStrategy? evictionStrategy = null)
    {
        var adapter = new TestEventHubDataAdapter(serializer);
        evictionStrategy ??= new EventHubQueueCacheFactory.EventHubCacheEvictionStrategy(
            NullLogger.Instance,
            new TimePurgePredicate(TimeSpan.FromDays(1), TimeSpan.FromDays(1)),
            null,
            null);
        return new EventHubQueueCache(
            partition,
            EventHubAdapterReceiver.MaxMessagesPerRead,
            pool,
            adapter,
            evictionStrategy,
            NoOpCheckpointer.Instance,
            NullLogger.Instance,
            null,
            null,
            null,
            controller);
    }

    private static EventData MakeEventData(long sequenceNumber, int payloadSize = 2)
    {
        var now = DateTime.UtcNow;
        return EventHubsModelFactory.EventData(
            eventBody: new BinaryData(new byte[payloadSize]),
            offsetString: now.Ticks.ToString(CultureInfo.InvariantCulture),
            sequenceNumber: sequenceNumber,
            enqueuedTime: now);
    }

    private sealed class TestEventHubDataAdapter(Serializer serializer) : EventHubDataAdapter(serializer)
    {
        public override StreamPosition GetStreamPosition(string partition, EventData queueMessage)
        {
            var streamId = StreamId.Create("test", $"{partition}-{queueMessage.SequenceNumber}");
            var token = new EventHubSequenceTokenV2(queueMessage.OffsetString, queueMessage.SequenceNumber, 0);
            return new StreamPosition(streamId, token);
        }
    }

    private sealed class TrackingEvictionStrategy : IEvictionStrategy, IDisposable
    {
        private IPurgeObservable? purgeObservable;

        public IPurgeObservable PurgeObservable
        {
            set => purgeObservable = value;
        }

        public Action<CachedMessage?, CachedMessage?>? OnPurged { get; set; }

        public bool WasCacheEmptyOnDispose { get; private set; }

        public void Dispose() => WasCacheEmptyOnDispose = purgeObservable?.IsEmpty == true;

        public void OnBlockAllocated(FixedSizeBuffer newBlock)
        {
        }

        public void PerformPurge(DateTime utcNow)
        {
        }
    }

    private sealed class CustomBufferPoolFactory : EventHubQueueCacheFactory
    {
        private readonly IObjectPool<FixedSizeBuffer> bufferPool =
            new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(1024 * 1024));

        public CustomBufferPoolFactory(IEventHubDataAdapter adapter, OrleansInstruments instruments)
            : base(
                new EventHubStreamCachePressureOptions { AveragingCachePressureMonitorFlowControlThreshold = null },
                new EventHubStreamCacheMemoryOptions { MaxActiveCacheMemory = 1, MaxBufferPoolMemory = 0 },
                new StreamCacheEvictionOptions(),
                new StreamStatisticOptions(),
                adapter,
                new EventHubMonitorAggregationDimensions("test"),
                instruments)
        {
        }

        protected override IObjectPool<FixedSizeBuffer> CreateBufferPool(
            StreamStatisticOptions statisticOptions,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            EventHubMonitorAggregationDimensions sharedDimensions,
            out string blockPoolId)
        {
            blockPoolId = "custom";
            return bufferPool;
        }
    }

    private sealed class ExposedBufferPoolFactory : EventHubQueueCacheFactory
    {
        private readonly StreamStatisticOptions statisticOptions = new();
        private readonly EventHubMonitorAggregationDimensions dimensions = new("test");

        public ExposedBufferPoolFactory(IEventHubDataAdapter adapter, OrleansInstruments instruments)
            : base(
                new EventHubStreamCachePressureOptions(),
                new EventHubStreamCacheMemoryOptions(),
                new StreamCacheEvictionOptions(),
                new StreamStatisticOptions(),
                adapter,
                new EventHubMonitorAggregationDimensions("test"),
                instruments)
        {
        }

        public IObjectPool<FixedSizeBuffer> GetBufferPool()
            => base.CreateBufferPool(statisticOptions, NullLoggerFactory.Instance, dimensions, out _);
    }
}
