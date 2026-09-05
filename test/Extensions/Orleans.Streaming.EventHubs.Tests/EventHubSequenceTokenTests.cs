using Orleans.Providers.Streams.Common;
using Orleans.Streaming.EventHubs;
using Orleans.Streams;
using UnitTests.StreamingTests;
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
    public void InheritedEventHubContractsRemainSymmetricAndTransitive()
    {
        StreamSequenceToken first = new CustomEventHubSequenceToken("10", 10, 2);
        StreamSequenceToken second = new CustomEventHubSequenceToken("other-offset", 10, 2);
        StreamSequenceToken builtIn = new EventHubSequenceToken("10", 10, 2);

        Assert.True(first.Equals(second));
        Assert.True(second.Equals(first));
        Assert.Equal(0, first.CompareTo(second));
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        StreamSequenceToken[] tokens =
        [
            first,
            second,
            builtIn,
            new EventHubSequenceTokenV2("v2-offset", 10, 2),
            new CustomEventHubSequenceTokenV2("custom-v2-offset", 10, 2),
        ];
        foreach (var left in tokens)
        {
            foreach (var right in tokens)
            {
                Assert.True(left.Equals(right));
                Assert.True(left.Equals((object)right));
                Assert.Equal(0, left.CompareTo(right));
                Assert.Equal(left.GetHashCode(), right.GetHashCode());
            }

            var generic = new EventSequenceToken(10, 2);
            Assert.False(left.Equals(generic));
            Assert.False(generic.Equals(left));
            Assert.Throws<ArgumentOutOfRangeException>(() => left.CompareTo(generic));
            Assert.Throws<ArgumentOutOfRangeException>(() => generic.CompareTo(left));
        }

        Assert.Single(new HashSet<StreamSequenceToken>(tokens));
        Assert.Single(new SortedSet<StreamSequenceToken>(Enumerable.Reverse(tokens)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LegacyFactoryBaseToken_InitialHandshakeRecoversCustomEventHubEvents(bool v2)
        => LegacyTokenRecoveryFixture.VerifyRecovery(
            LegacyTokenRecoveryFixture.LoadLegacyToken(v2: false),
            (sequence, index) => CreateCustomToken(v2, sequence, index),
            acknowledged: false,
            renegotiate: false,
            token => AssertCustomMetadata(token, v2));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task LegacyFactoryBaseToken_DeliveryHandshakeRecoversCustomEventHubEvents(bool v2, bool acknowledged)
        => LegacyTokenRecoveryFixture.VerifyRecovery(
            LegacyTokenRecoveryFixture.LoadLegacyToken(v2: false),
            (sequence, index) => CreateCustomToken(v2, sequence, index),
            acknowledged,
            renegotiate: true,
            token => AssertCustomMetadata(token, v2));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task LegacyAcknowledgedToken_RecognizesCustomEventHubDuplicate(bool v2, bool batch)
        => LegacyTokenRecoveryFixture.VerifyDuplicate(
            LegacyTokenRecoveryFixture.LoadLegacyToken(v2: false),
            CreateCustomToken(v2, 500, 2),
            batch);

    [Fact]
    public void OverriddenEventHubContract_KeepsLegacyGenericTokensIsolated()
        => LegacyTokenRecoveryFixture.AssertProviderIsolation(new OverriddenEventHubSequenceToken("offset", 500, 2));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EventHubLegacyRecovery_RequiresTheHistoricalV1FactoryShape(bool v2)
        => LegacyTokenRecoveryFixture.AssertRecoveryIncompatible(
            CreateCustomToken(v2, 500, 2),
            LegacyTokenRecoveryFixture.LoadLegacyToken(v2: true));

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

    private static EventHubSequenceToken CreateCustomToken(bool v2, long sequence, int index)
    {
        EventHubSequenceToken batchToken = v2
            ? new CustomEventHubSequenceTokenV2($"offset-{sequence}", sequence, 0)
            : new CustomEventHubSequenceToken($"offset-{sequence}", sequence, 0);
        return Assert.IsAssignableFrom<EventHubSequenceToken>(batchToken.CreateSequenceTokenForEvent(index));
    }

    private static void AssertCustomMetadata(StreamSequenceToken token, bool v2)
    {
        var metadata = v2
            ? Assert.IsType<CustomEventHubSequenceTokenV2>(token).Metadata
            : Assert.IsType<CustomEventHubSequenceToken>(token).Metadata;
        Assert.Equal("custom-metadata", metadata);
        Assert.Equal($"offset-{token.SequenceNumber}", Assert.IsAssignableFrom<EventHubSequenceToken>(token).EventHubOffset);
    }

    private sealed class CustomEventHubSequenceToken(
        string eventHubOffset,
        long sequenceNumber,
        int eventIndex)
        : EventHubSequenceToken(eventHubOffset, sequenceNumber, eventIndex)
    {
        public string Metadata { get; } = "custom-metadata";
    }

    private sealed class CustomEventHubSequenceTokenV2(
        string eventHubOffset,
        long sequenceNumber,
        int eventIndex)
        : EventHubSequenceTokenV2(eventHubOffset, sequenceNumber, eventIndex)
    {
        public string Metadata { get; } = "custom-metadata";
    }

    private sealed class OverriddenEventHubSequenceToken(string offset, long sequence, int index)
        : EventHubSequenceToken(offset, sequence, index)
    {
        public override int CompareTo(StreamSequenceToken? other) => base.CompareTo(other);
    }
}
