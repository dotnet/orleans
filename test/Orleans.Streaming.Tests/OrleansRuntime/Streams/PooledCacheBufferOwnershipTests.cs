using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
public sealed class PooledCacheBufferOwnershipTests
{
    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void MemoryCache_EmptyPurgeAllocatesFreshBufferForNextMessage()
    {
        var pool = new TrackingBufferPool();
        var serializer = new TestMemorySerializer();
        var cache = new MemoryPooledCache<TestMemorySerializer>(
            pool,
            new AlwaysPurgePredicate(),
            NullLogger.Instance,
            serializer,
            cacheMonitor: null,
            monitorWriteInterval: null,
            purgeMetadataInterval: null);
        var streamId = StreamId.Create("namespace", Guid.NewGuid());

        cache.AddToCache([CreateMemoryBatch(streamId, 1, serializer)]);
        Assert.False(cache.TryPurgeFromCache(out _));
        Assert.Equal(1, pool.FreeCount);

        cache.AddToCache([CreateMemoryBatch(streamId, 2, serializer)]);

        Assert.Equal(2, pool.AllocateCount);
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void GeneratorCache_EmptyPurgeAllocatesFreshBufferForNextMessage()
    {
        var pool = new TrackingBufferPool();
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var cache = new GeneratorPooledCache(
            pool,
            NullLogger.Instance,
            services.GetRequiredService<Serializer>(),
            cacheMonitor: null,
            monitorWriteInterval: null,
            new AlwaysPurgePredicate());
        var streamId = StreamId.Create("namespace", Guid.NewGuid());

        cache.AddToCache([new GeneratedBatchContainer(streamId, 1, new EventSequenceTokenV2(1))]);
        Assert.False(cache.TryPurgeFromCache(out _));
        Assert.Equal(1, pool.FreeCount);

        cache.AddToCache([new GeneratedBatchContainer(streamId, 2, new EventSequenceTokenV2(2))]);

        Assert.Equal(2, pool.AllocateCount);
    }

    private static MemoryBatchContainer<TestMemorySerializer> CreateMemoryBatch(
        StreamId streamId,
        long sequenceNumber,
        TestMemorySerializer serializer)
        => new(
            new MemoryMessageData
            {
                StreamId = streamId,
                SequenceNumber = sequenceNumber,
                EnqueueTimeUtc = DateTime.UtcNow,
                Payload = new byte[] { 1 },
            },
            serializer);

    private sealed class AlwaysPurgePredicate : TimePurgePredicate
    {
        public AlwaysPurgePredicate()
            : base(TimeSpan.Zero, TimeSpan.Zero)
        {
        }

        public override bool ShouldPurgeFromTime(TimeSpan timeInCache, TimeSpan relativeAge) => true;
    }

    private sealed class TrackingBufferPool : IObjectPool<FixedSizeBuffer>
    {
        public int AllocateCount { get; private set; }

        public int FreeCount { get; private set; }

        public FixedSizeBuffer Allocate()
        {
            AllocateCount++;
            return new FixedSizeBuffer(4 * 1024) { Pool = this };
        }

        public void Free(FixedSizeBuffer resource)
        {
            FreeCount++;
        }
    }

    private sealed class TestMemorySerializer : IMemoryMessageBodySerializer
    {
        public ArraySegment<byte> Serialize(MemoryMessageBody body) => new byte[] { 1 };

        public MemoryMessageBody Deserialize(ArraySegment<byte> bodyBytes) => new([], requestContext: null);
    }
}
