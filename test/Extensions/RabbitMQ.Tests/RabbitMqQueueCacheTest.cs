using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streaming.RabbitMQ.Adapters;
using Orleans.Streaming.RabbitMQ.Adapters.Cache;
using Orleans.Streaming.RabbitMQ.RabbitMQ;
using Xunit;

namespace RabbitMQ.Tests;

public class RabbitMqQueueCacheTest
{
    [Fact]
    public void PurgingDeliveredMessage_ReleasesCachePressure()
    {
        var streamId = StreamId.Create("test", Guid.NewGuid());
        var cache = new RabbitMqQueueCache(new RabbitMqQueueCacheOptions { CacheSize = 1 });
        cache.AddToCache([CreateBatch(streamId, 0)]);
        var cursor = cache.GetCacheCursor(streamId, new EventSequenceTokenV2(0));

        Assert.True(cache.IsUnderPressure());
        Assert.True(cursor.MoveNext());
        Assert.False(cursor.MoveNext());
        Assert.True(cache.TryPurgeFromCache(out var purgedItems));
        Assert.Single(purgedItems);
        Assert.False(cache.IsUnderPressure());
    }

    [Fact]
    public void FailedDelivery_RemainsCachedUntilRetrySucceeds()
    {
        var streamId = StreamId.Create("test", Guid.NewGuid());
        var cache = new RabbitMqQueueCache(new RabbitMqQueueCacheOptions { CacheSize = 1 });
        cache.AddToCache([CreateBatch(streamId, 0)]);
        var cursor = cache.GetCacheCursor(streamId, new EventSequenceTokenV2(0));

        Assert.True(cursor.MoveNext());
        var failedMessage = cursor.GetCurrent(out _);
        cursor.RecordDeliveryFailure();

        Assert.True(cursor.MoveNext());
        Assert.Same(failedMessage, cursor.GetCurrent(out _));
        Assert.False(cache.TryPurgeFromCache(out _));
        Assert.True(cache.IsUnderPressure());

        Assert.False(cursor.MoveNext());
        Assert.True(cache.TryPurgeFromCache(out var purgedItems));
        Assert.Single(purgedItems);
        Assert.False(cache.IsUnderPressure());
    }

    private static RabbitMqBatchContainer CreateBatch(StreamId streamId, long sequenceNumber) =>
        new(streamId, [new object()], new EventSequenceTokenV2(sequenceNumber));
}
