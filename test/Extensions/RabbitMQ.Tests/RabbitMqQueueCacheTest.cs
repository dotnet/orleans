using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streaming.RabbitMQ.Adapters;
using Orleans.Streaming.RabbitMQ.Adapters.Cache;
using Orleans.Streaming.RabbitMQ.RabbitMQ;
using Orleans.Streams;
using Xunit;

namespace RabbitMQ.Tests;

[TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
public class RabbitMqQueueCacheTest
{
    [Fact]
    public void PurgeWaitsForEveryCursorToDeliverContiguousBatch()
    {
        var streamId = StreamId.Create("test", Guid.NewGuid());
        var cache = CreateCache();
        cache.AddToCache([CreateBatch(streamId, 0), CreateBatch(streamId, 1)]);
        using var fast = cache.GetCacheCursor(streamId, null);
        using var slow = cache.GetCacheCursor(streamId, null);
        Assert.True(fast.MoveNext());
        Assert.True(slow.MoveNext());

        Assert.True(fast.MoveNext());
        Assert.False(cache.TryPurgeFromCache(out _));

        Assert.True(slow.MoveNext());
        Assert.True(cache.TryPurgeFromCache(out var firstPurge));
        Assert.Equal(0, Assert.Single(firstPurge).SequenceToken.SequenceNumber);
        Assert.False(fast.MoveNext());
        Assert.False(cache.TryPurgeFromCache(out _));

        Assert.False(slow.MoveNext());
        Assert.True(cache.TryPurgeFromCache(out var secondPurge));
        Assert.Equal(1, Assert.Single(secondPurge).SequenceToken.SequenceNumber);
    }

    [Fact]
    public void StreamWithoutCursorIsPurgeableWithoutSkippingActiveStream()
    {
        var inactiveStream = StreamId.Create("inactive", Guid.NewGuid());
        var activeStream = StreamId.Create("active", Guid.NewGuid());
        var cache = CreateCache();
        using var cursor = cache.GetCacheCursor(activeStream, null);
        cache.AddToCache(
        [
            CreateBatch(inactiveStream, 0),
            CreateBatch(activeStream, 1),
            CreateBatch(inactiveStream, 2)
        ]);

        Assert.True(cache.TryPurgeFromCache(out var initialPurge));
        Assert.Equal(0, Assert.Single(initialPurge).SequenceToken.SequenceNumber);
        Assert.True(cursor.MoveNext());
        Assert.False(cursor.MoveNext());
        Assert.True(cache.TryPurgeFromCache(out var finalPurge));
        Assert.Equal([1L, 2L], finalPurge.Select(item => item.SequenceToken.SequenceNumber));
    }

    [Fact]
    public void DisposingCursorReleasesItsUndeliveredBatches()
    {
        var streamId = StreamId.Create("test", Guid.NewGuid());
        var cache = CreateCache();
        cache.AddToCache([CreateBatch(streamId, 0)]);
        var cursor = cache.GetCacheCursor(streamId, null);

        cursor.Dispose();

        Assert.True(cache.TryPurgeFromCache(out var purged));
        Assert.Single(purged);
    }

    [Fact]
    public void FailedDeliveryRemainsCachedUntilRetrySucceeds()
    {
        var streamId = StreamId.Create("test", Guid.NewGuid());
        var cache = new RabbitMqQueueCache(new RabbitMQQueueCacheOptions { CacheSize = 1 });
        cache.AddToCache([CreateBatch(streamId, 0)]);
        using var cursor = cache.GetCacheCursor(streamId, null);
        Assert.True(cursor.MoveNext());

        cursor.RecordDeliveryFailure();

        Assert.True(cursor.MoveNext());
        Assert.False(cache.TryPurgeFromCache(out _));
        Assert.True(cache.IsUnderPressure());
        Assert.False(cursor.MoveNext());
        Assert.True(cache.TryPurgeFromCache(out var purgedItems));
        Assert.Single(purgedItems);
        Assert.False(cache.IsUnderPressure());
    }

    [Fact]
    public void DeliveryFailureStateIsIsolatedPerCursor()
    {
        var streamId = StreamId.Create("test", Guid.NewGuid());
        var cache = CreateCache();
        cache.AddToCache([CreateBatch(streamId, 0), CreateBatch(streamId, 1)]);
        using var failedCursor = cache.GetCacheCursor(streamId, null);
        using var successfulCursor = cache.GetCacheCursor(streamId, null);
        Assert.True(failedCursor.MoveNext());
        Assert.True(successfulCursor.MoveNext());
        failedCursor.RecordDeliveryFailure();

        Assert.True(successfulCursor.MoveNext());
        Assert.Equal(1, successfulCursor.GetCurrent(out _)!.SequenceToken.SequenceNumber);
        Assert.True(failedCursor.MoveNext());
        Assert.Equal(0, failedCursor.GetCurrent(out _)!.SequenceToken.SequenceNumber);
        Assert.False(cache.TryPurgeFromCache(out _));
    }

    [Fact]
    public void PurgedHighWatermarksRemainBoundedByCacheCapacity()
    {
        var cache = new RabbitMqQueueCache(new RabbitMQQueueCacheOptions { CacheSize = 3 });
        var streams = Enumerable.Range(0, 10)
            .Select(index => StreamId.Create("test", index.ToString()))
            .ToArray();
        cache.AddToCache(streams.Select((streamId, index) => CreateBatch(streamId, index)).Cast<IBatchContainer>().ToList());

        Assert.True(cache.TryPurgeFromCache(out var purged));
        Assert.Equal(10, purged.Count);
        Assert.Equal(3, cache.PurgedHighWatermarkCount);
        Assert.Throws<QueueCacheMissException>(
            () => cache.GetCacheCursor(streams[^1], new EventSequenceTokenV2(8)));
        using var evictedStreamCursor = cache.GetCacheCursor(streams[0], new EventSequenceTokenV2(0));
    }

    [Fact]
    public void DuplicateOffsetsDoNotPinTheCache()
    {
        var streamId = StreamId.Create("test", Guid.NewGuid());
        var cache = CreateCache();
        cache.AddToCache([CreateBatch(streamId, 0), CreateBatch(streamId, 0)]);
        using var cursor = cache.GetCacheCursor(streamId, null);

        Assert.True(cursor.MoveNext());
        Assert.False(cursor.MoveNext());

        Assert.True(cache.TryPurgeFromCache(out var purged));
        Assert.Equal(2, purged.Count);
        Assert.False(cache.IsUnderPressure());
    }

    private static RabbitMqQueueCache CreateCache() =>
        new(new RabbitMQQueueCacheOptions { CacheSize = 10 });

    private static RabbitMqBatchContainer CreateBatch(StreamId streamId, long sequenceNumber) =>
        new(streamId, [new object()], new EventSequenceTokenV2(sequenceNumber));
}
