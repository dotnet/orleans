using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
public class SimpleQueueCacheTests
{
    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void EarliestAvailableStartsAtOldestRetainedMessageForStream()
    {
        var cache = new SimpleQueueCache(10, NullLogger.Instance);
        var targetStream = StreamId.Create("namespace", Guid.NewGuid());
        var otherStream = StreamId.Create("namespace", Guid.NewGuid());
        cache.AddToCache(
        [
            new TestBatchContainer(targetStream, 1),
            new TestBatchContainer(otherStream, 2),
            new TestBatchContainer(targetStream, 3),
        ]);

        var cursor = cache.GetCacheCursorAtPosition(targetStream, StreamSubscriptionStartPosition.EarliestAvailable);

        Assert.True(cursor.MoveNext());
        Assert.Equal(1, cursor.GetCurrent(out _)!.SequenceToken.SequenceNumber);
        Assert.True(cursor.MoveNext());
        Assert.Equal(3, cursor.GetCurrent(out _)!.SequenceToken.SequenceNumber);
        Assert.False(cursor.MoveNext());
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void EarliestAvailableWaitsForFirstFutureMatchingMessage()
    {
        var cache = new SimpleQueueCache(10, NullLogger.Instance);
        var targetStream = StreamId.Create("namespace", Guid.NewGuid());
        var otherStream = StreamId.Create("namespace", Guid.NewGuid());
        cache.AddToCache([new TestBatchContainer(otherStream, 100)]);
        var cursor = cache.GetCacheCursorAtPosition(targetStream, StreamSubscriptionStartPosition.EarliestAvailable);

        Assert.False(cursor.MoveNext());

        var unrelatedToken = new EventSequenceTokenV2(101);
        cache.AddToCache([new TestBatchContainer(otherStream, unrelatedToken)]);
        cursor.Refresh(unrelatedToken);
        Assert.False(cursor.MoveNext());

        var targetToken = new EventSequenceTokenV2(1);
        cache.AddToCache([new TestBatchContainer(targetStream, targetToken)]);
        cursor.Refresh(targetToken);

        Assert.True(cursor.MoveNext());
        Assert.Equal(targetToken, cursor.GetCurrent(out _)!.SequenceToken);
    }

    private sealed class TestBatchContainer : IBatchContainer
    {
        public TestBatchContainer(StreamId streamId, long sequenceNumber)
            : this(streamId, new EventSequenceTokenV2(sequenceNumber))
        {
        }

        public TestBatchContainer(StreamId streamId, StreamSequenceToken token)
        {
            StreamId = streamId;
            SequenceToken = token;
        }

        public StreamId StreamId { get; }
        public StreamSequenceToken SequenceToken { get; }
        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>() => [];
        public bool ImportRequestContext() => false;
    }
}
