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
}
