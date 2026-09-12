using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streaming.RabbitMQ.Adapters;
using Orleans.Streaming.RabbitMQ.Adapters.Cache;
using Orleans.Streaming.RabbitMQ.RabbitMQ;
using Orleans.Streams;
using Xunit;

namespace RabbitMQ.Tests;

[TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
public class RabbitMqQueueCacheCursorTest
{
    [Fact]
    public void NullTokenStartsAtEarliestAvailableBatch()
    {
        var streamId = StreamId.Create("test", Guid.NewGuid());
        var cache = CreateCache();
        cache.AddToCache([CreateBatch(streamId, 4), CreateBatch(streamId, 5)]);

        using var cursor = cache.GetCacheCursor(streamId, null);

        Assert.True(cursor.MoveNext());
        var current = cursor.GetCurrent(out _);
        Assert.NotNull(current);
        Assert.Equal(4, current.SequenceToken.SequenceNumber);
    }

    [Fact]
    public void TokenOlderThanCachedRangeThrowsQueueCacheMiss()
    {
        var streamId = StreamId.Create("test", Guid.NewGuid());
        var cache = CreateCache();
        cache.AddToCache([CreateBatch(streamId, 4), CreateBatch(streamId, 5)]);

        var requested = new EventSequenceTokenV2(3);
        var exception = Assert.Throws<QueueCacheMissException>(
            () => cache.GetCacheCursor(streamId, requested));

        Assert.Equal(requested.ToString(), exception.Requested);
        Assert.Equal(new EventSequenceTokenV2(4).ToString(), exception.Low);
        Assert.Equal(new EventSequenceTokenV2(5).ToString(), exception.High);
    }

    [Fact]
    public void FutureTokenWaitsForRequestedBatch()
    {
        var streamId = StreamId.Create("test", Guid.NewGuid());
        var cache = CreateCache();
        cache.AddToCache([CreateBatch(streamId, 4)]);
        using var cursor = cache.GetCacheCursor(streamId, new EventSequenceTokenV2(6));

        Assert.False(cursor.MoveNext());
        cache.AddToCache([CreateBatch(streamId, 5), CreateBatch(streamId, 6)]);

        Assert.True(cursor.MoveNext());
        var current = cursor.GetCurrent(out _);
        Assert.NotNull(current);
        Assert.Equal(6, current.SequenceToken.SequenceNumber);
    }

    [Fact]
    public void DeliveryFailureRetriesCurrentBatch()
    {
        var streamId = StreamId.Create("test", Guid.NewGuid());
        var cache = CreateCache();
        cache.AddToCache([CreateBatch(streamId, 0), CreateBatch(streamId, 1)]);
        using var cursor = cache.GetCacheCursor(streamId, null);
        Assert.True(cursor.MoveNext());
        var failed = cursor.GetCurrent(out _);

        cursor.RecordDeliveryFailure();

        Assert.True(cursor.MoveNext());
        Assert.Same(failed, cursor.GetCurrent(out _));
        Assert.True(cursor.MoveNext());
        var current = cursor.GetCurrent(out _);
        Assert.NotNull(current);
        Assert.Equal(1, current.SequenceToken.SequenceNumber);
    }

    private static RabbitMqQueueCache CreateCache() =>
        new(new RabbitMQQueueCacheOptions { CacheSize = 10 });

    private static RabbitMqBatchContainer CreateBatch(StreamId streamId, long sequenceNumber) =>
        new(streamId, [new object()], new EventSequenceTokenV2(sequenceNumber));
}
