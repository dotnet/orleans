using Orleans.Providers.Streams.Common;
using Orleans.Streaming.EventHubs;
using Orleans.Streams;
using Xunit;

namespace UnitTests.Streaming;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
public sealed class EventHubSequenceTokenTests
{
    [Fact]
    public void EventHubVersionsRemainSymmetricAndRejectBaseTokens()
    {
        StreamSequenceToken v1 = new EventHubSequenceToken("100", 10, 2);
        StreamSequenceToken v2 = new EventHubSequenceTokenV2("100", 10, 2);
        StreamSequenceToken baseToken = new EventSequenceToken(10, 2);

        Assert.True(v1.Equals(v2));
        Assert.True(v2.Equals(v1));
        Assert.Equal(0, v1.CompareTo(v2));
        Assert.Equal(0, v2.CompareTo(v1));
        Assert.Equal(v1.GetHashCode(), v2.GetHashCode());
        Assert.False(v1.Equals(baseToken));
        Assert.False(baseToken.Equals(v1));
        Assert.Throws<ArgumentOutOfRangeException>(() => v1.CompareTo(baseToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => baseToken.CompareTo(v1));
    }

    [Fact]
    public void EventHubOrderingUsesSequenceBeforeEventIndexAcrossVersions()
    {
        StreamSequenceToken olderSequence = new EventHubSequenceToken("9", 9, 99);
        StreamSequenceToken newerSequence = new EventHubSequenceTokenV2("10", 10, 0);
        StreamSequenceToken earlierEvent = new EventHubSequenceTokenV2("10", 10, 1);
        StreamSequenceToken laterEvent = new EventHubSequenceToken("10", 10, 2);

        Assert.True(olderSequence.CompareTo(newerSequence) < 0);
        Assert.True(newerSequence.CompareTo(olderSequence) > 0);
        Assert.True(earlierEvent.CompareTo(laterEvent) < 0);
        Assert.True(laterEvent.CompareTo(earlierEvent) > 0);
    }

    [Fact]
    public void SameCustomEventHubTokenSubtypeRemainsComparable()
    {
        StreamSequenceToken first = new CustomEventHubSequenceToken("10", 10, 2);
        StreamSequenceToken second = new CustomEventHubSequenceToken("other-offset", 10, 2);
        StreamSequenceToken builtIn = new EventHubSequenceToken("10", 10, 2);

        Assert.True(first.Equals(second));
        Assert.True(second.Equals(first));
        Assert.Equal(0, first.CompareTo(second));
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.False(first.Equals(builtIn));
        Assert.False(builtIn.Equals(first));
        Assert.Throws<ArgumentOutOfRangeException>(() => first.CompareTo(builtIn));
        Assert.Throws<ArgumentOutOfRangeException>(() => builtIn.CompareTo(first));
    }

    [Fact]
    public void CreateSequenceTokenForEventPreservesConcreteTypeAndOffset()
    {
        var batchToken = new EventHubSequenceTokenV2("offset-10", 10, 0);

        var eventToken = Assert.IsType<EventHubSequenceTokenV2>(
            batchToken.CreateSequenceTokenForEvent(2));

        Assert.Equal("offset-10", eventToken.EventHubOffset);
        Assert.Equal(10, eventToken.SequenceNumber);
        Assert.Equal(2, eventToken.EventIndex);
        Assert.True(batchToken.CompareTo(eventToken) < 0);
    }

    private sealed class CustomEventHubSequenceToken(
        string eventHubOffset,
        long sequenceNumber,
        int eventIndex)
        : EventHubSequenceToken(eventHubOffset, sequenceNumber, eventIndex);
}
