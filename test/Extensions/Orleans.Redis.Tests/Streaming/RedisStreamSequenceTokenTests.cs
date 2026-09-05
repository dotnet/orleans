using Orleans.Providers.Streams.Common;
using Orleans.Streaming.Redis;
using Orleans.Streams;
using UnitTests.StreamingTests;
using Xunit;

namespace Tester.Redis.Streaming;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
public sealed class RedisStreamSequenceTokenTests
{
    [Fact]
    public void LegacyGenericRecovery_PreservesRedisEntryIdentity()
    {
        var first = new RedisStreamSequenceToken("10-1", 10, 1, 2);
        var later = new RedisStreamSequenceToken("10-2", 10, 2, 0);
        var eventToken = first.CreateSequenceTokenForEvent(3);

        LegacyTokenRecoveryFixture.AssertProviderIsolation(first);
        Assert.True(first.CompareTo(later) < 0);
        Assert.True(later.CompareTo(first) > 0);
        Assert.Equal("10-1", eventToken.EntryId);
        Assert.Equal(1, eventToken.RedisSequenceNumber);
        Assert.Equal(3, eventToken.EventIndex);
        Assert.Equal(2, first.EventIndex);
    }

    [Fact]
    public void RedisTokensUseOneSymmetricEqualityAndOrderingContract()
    {
        StreamSequenceToken first = new RedisStreamSequenceToken("10-1", 10, 1, 0);
        StreamSequenceToken equal = new RedisStreamSequenceToken("10-1", 10, 1, 0);
        StreamSequenceToken differentEntry = new RedisStreamSequenceToken("010-1", 10, 1, 0);
        StreamSequenceToken baseToken = new EventSequenceTokenV2(10, 0);

        Assert.True(first.Equals(equal));
        Assert.True(equal.Equals(first));
        Assert.Equal(0, first.CompareTo(equal));
        Assert.Equal(first.GetHashCode(), equal.GetHashCode());
        Assert.NotEqual(0, first.CompareTo(differentEntry));
        Assert.Equal(-Math.Sign(first.CompareTo(differentEntry)), Math.Sign(differentEntry.CompareTo(first)));
        Assert.False(first.Equals(differentEntry));
        Assert.False(differentEntry.Equals(first));
        Assert.False(first.Equals(baseToken));
        Assert.False(baseToken.Equals(first));
        Assert.Throws<ArgumentOutOfRangeException>(() => first.CompareTo(baseToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => baseToken.CompareTo(first));
        Assert.Single(new Dictionary<StreamSequenceToken, string>
        {
            [first] = "first",
            [equal] = "equal",
        });
        Assert.Equal(2, new Dictionary<StreamSequenceToken, string>
        {
            [first] = "first",
            [differentEntry] = "different",
        }.Count);
        Assert.Equal(2, new SortedSet<StreamSequenceToken> { first, differentEntry }.Count);
    }
}
