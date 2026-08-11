using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Serialization;
using Orleans.Streaming.Redis;
using Orleans.Streams;
using StackExchange.Redis;
using Xunit;

namespace Tester.Redis.Streaming;

[TestSuite("BVT")]
[TestProvider("Redis")]
[TestArea("Streaming")]
[TestCategory("Redis"), TestCategory("Streaming")]
public sealed class RedisStreamBatchContainerTests
{
    [Fact]
    public void RedisStreamBatchContainer_FromStreamEntry_PreservesStreamIdAndCreatesRedisSequenceTokens()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer<RedisStreamBatchContainer>>();
        var streamId = StreamId.Create(nameof(RedisStreamBatchContainerTests), Guid.NewGuid());
        var payload = RedisStreamBatchContainer.ToRedisValue(
            serializer,
            streamId,
            new object[] { 1, 2 },
            new Dictionary<string, object>());
        var entry = new StreamEntry("1735790000000-3", [new NameValueEntry(RedisStreamReceiverOptions.DefaultFieldName, payload)]);

        var container = RedisStreamBatchContainer.FromStreamEntry(serializer, entry, RedisStreamReceiverOptions.DefaultFieldName);

        Assert.Equal(streamId, container.StreamId);
        Assert.Equal(streamId.GetNamespace(), container.StreamId.GetNamespace());
        Assert.Equal(streamId.GetKeyAsString(), container.StreamId.GetKeyAsString());

        var batchToken = Assert.IsType<RedisStreamSequenceToken>(container.SequenceToken);
        Assert.Equal("1735790000000-3", batchToken.EntryId);
        Assert.Equal(1735790000000L, batchToken.SequenceNumber);
        Assert.Equal(3L, batchToken.RedisSequenceNumber);

        var events = container.GetEvents<object>().ToArray();
        var firstToken = Assert.IsType<RedisStreamSequenceToken>(events[0].Item2);
        var secondToken = Assert.IsType<RedisStreamSequenceToken>(events[1].Item2);
        Assert.Equal(0, firstToken.EventIndex);
        Assert.Equal(1, secondToken.EventIndex);
        Assert.True(firstToken.CompareTo(secondToken) < 0);
    }

    [Fact]
    public void RedisStreamSequenceToken_CompareTo_UsesRedisSequenceNumberBeforeEventIndex()
    {
        var first = new RedisStreamSequenceToken("100-1", 100, 1, 0);
        var sameEntryNextEvent = first.CreateSequenceTokenForEvent(1);
        var nextEntrySameMillisecond = new RedisStreamSequenceToken("100-2", 100, 2, 0);
        var nextMillisecond = new RedisStreamSequenceToken("101-0", 101, 0, 0);

        Assert.True(first.CompareTo(sameEntryNextEvent) < 0);
        Assert.True(sameEntryNextEvent.CompareTo(nextEntrySameMillisecond) < 0);
        Assert.True(nextEntrySameMillisecond.CompareTo(nextMillisecond) < 0);
    }
}
