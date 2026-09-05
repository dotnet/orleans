using System;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Orleans.Serialization;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;
using Orleans.Streaming.Kinesis;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;

namespace Orleans.Streaming.Kinesis.Tests;

[TestSuite("BVT")]
[TestArea("Streaming")]
[TestProvider("Kinesis")]
[TestCategory("AWS"), TestCategory("Kinesis")]
[Collection(TestEnvironmentFixture.DefaultCollection)]
public sealed class KinesisSequenceTokenTests
{
    // Representative of real Kinesis shard sequence numbers (up to 128 bits), which are far beyond
    // long.MaxValue (~9.22e18, 19 digits).
    private const string HugeShardSequence = "170141183460469231731687303715884105727";
    private const string SlightlyLargerShardSequence = "170141183460469231731687303715884105728";

    private readonly Serializer<KinesisSequenceToken> serializer;

    public KinesisSequenceTokenTests(TestEnvironmentFixture fixture)
    {
        serializer = fixture.Services.GetRequiredService<Serializer<KinesisSequenceToken>>();
    }

    [Fact]
    public void CompareToOrdersByShardSequenceMagnitudeBeyondInt64Range()
    {
        var older = new KinesisSequenceToken(HugeShardSequence, sequenceNumber: 0, eventIndex: 0);
        var newer = new KinesisSequenceToken(SlightlyLargerShardSequence, sequenceNumber: 0, eventIndex: 0);

        Assert.True(older.CompareTo(newer) < 0);
        Assert.True(newer.CompareTo(older) > 0);
        Assert.Equal(0, older.CompareTo(older));
        Assert.False(older.Equals(newer));
    }

    [Theory]
    [InlineData("9", "10")]
    [InlineData("99", "100")]
    [InlineData("999999999999999999", "1000000000000000000")]
    public void CompareToUsesNumericNotLexicographicOrdering(string smaller, string larger)
    {
        var smallerToken = new KinesisSequenceToken(smaller, sequenceNumber: 0, eventIndex: 0);
        var largerToken = new KinesisSequenceToken(larger, sequenceNumber: 0, eventIndex: 0);

        Assert.True(smallerToken.CompareTo(largerToken) < 0);
        Assert.True(string.CompareOrdinal(smaller, larger) > 0);
    }

    [Fact]
    public void CompareToBreaksTiesOnEventIndexWhenShardSequenceMatches()
    {
        var first = new KinesisSequenceToken(HugeShardSequence, sequenceNumber: 5, eventIndex: 0);
        var second = new KinesisSequenceToken(HugeShardSequence, sequenceNumber: 5, eventIndex: 1);

        Assert.True(first.CompareTo(second) < 0);
        Assert.True(second.CompareTo(first) > 0);
        Assert.False(first.Equals(second));

        var differentReceiverOrdinal = new KinesisSequenceToken(HugeShardSequence, sequenceNumber: 999, eventIndex: 0);
        Assert.Equal(0, first.CompareTo(differentReceiverOrdinal));
        Assert.True(first.Equals(differentReceiverOrdinal));
    }

    [Fact]
    public void EqualsIgnoresShardSequenceStringFormattingButRespectsNumericValue()
    {
        var zeroPadded = new KinesisSequenceToken("007", sequenceNumber: 1, eventIndex: 2);
        var unpadded = new KinesisSequenceToken("7", sequenceNumber: 999, eventIndex: 2);

        Assert.True(zeroPadded.Equals(unpadded));
        Assert.True(zeroPadded.Equals((object)unpadded));
        Assert.Equal(zeroPadded.GetHashCode(), unpadded.GetHashCode());
    }

    [Fact]
    public void EquivalentTokensCoalesceInHashAndSortedCollections()
    {
        StreamSequenceToken zeroPadded = new KinesisSequenceToken("007", sequenceNumber: 1, eventIndex: 2);
        StreamSequenceToken unpadded = new KinesisSequenceToken("7", sequenceNumber: 999, eventIndex: 2);

        Assert.Single(new HashSet<StreamSequenceToken> { zeroPadded, unpadded });
        Assert.Single(new SortedSet<StreamSequenceToken> { unpadded, zeroPadded });
    }

    [Fact]
    public void KinesisAndBaseTokensAreIncompatibleInBothDirections()
    {
        StreamSequenceToken kinesis = new KinesisSequenceToken("7", sequenceNumber: 1, eventIndex: 2);
        StreamSequenceToken baseToken = new EventSequenceTokenV2(1, 2);

        Assert.False(kinesis.Equals(baseToken));
        Assert.False(baseToken.Equals(kinesis));
        Assert.Throws<ArgumentOutOfRangeException>(() => kinesis.CompareTo(baseToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => baseToken.CompareTo(kinesis));
        Assert.Equal(2, new Dictionary<StreamSequenceToken, string>
        {
            [kinesis] = "kinesis",
            [baseToken] = "base",
        }.Count);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SortedSet<StreamSequenceToken> { kinesis, baseToken });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SortedSet<StreamSequenceToken> { baseToken, kinesis });
    }

    [Fact]
    public void LegacyGenericRecovery_PreservesKinesisIdentityAndAuthoritativeOffset()
    {
        var older = new KinesisSequenceToken(HugeShardSequence, sequenceNumber: 999, eventIndex: 2);
        var newer = new KinesisSequenceToken(SlightlyLargerShardSequence, sequenceNumber: 1, eventIndex: 0);
        var samePosition = new KinesisSequenceToken(HugeShardSequence, sequenceNumber: 1, eventIndex: 2);

        LegacyTokenRecoveryFixture.AssertProviderIsolation(older);
        Assert.True(older.CompareTo(newer) < 0);
        Assert.True(newer.CompareTo(older) > 0);
        Assert.True(older.Equals(samePosition));
        Assert.True(samePosition.Equals(older));
        Assert.Equal(0, older.CompareTo(samePosition));
        Assert.Equal(older.GetHashCode(), samePosition.GetHashCode());
    }

    [Fact]
    public void CreateSequenceTokenForEventPreservesKinesisPosition()
    {
        var batchToken = new KinesisSequenceToken(HugeShardSequence, sequenceNumber: 42, eventIndex: 0);

        var eventToken = Assert.IsType<KinesisSequenceToken>(batchToken.CreateSequenceTokenForEvent(3));

        Assert.Equal(HugeShardSequence, eventToken.ShardSequence);
        Assert.Equal(42, eventToken.SequenceNumber);
        Assert.Equal(3, eventToken.EventIndex);
        Assert.True(batchToken.CompareTo(eventToken) < 0);
    }

    [Fact]
    public void RepeatedOrderingOperationsDoNotReparseShardSequence()
    {
        var older = new KinesisSequenceToken(HugeShardSequence, sequenceNumber: 0, eventIndex: 0);
        var newer = new KinesisSequenceToken(SlightlyLargerShardSequence, sequenceNumber: 0, eventIndex: 0);

        _ = older.CompareTo(newer);
        _ = older.GetHashCode();
        _ = newer.GetHashCode();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var comparisonTotal = 0;
        var hash = 0;

        for (var i = 0; i < 1_000; i++)
        {
            comparisonTotal += older.CompareTo(newer);
            hash ^= older.GetHashCode();
            hash ^= newer.GetHashCode();
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.Equal(-1_000, comparisonTotal);
        Assert.Equal(0, allocated);
        GC.KeepAlive(hash);
    }

    [Fact]
    public void BinarySerializationRoundTripPreservesFieldsAndOrderingAfterRestart()
    {
        var original = new KinesisSequenceToken(HugeShardSequence, sequenceNumber: 42, eventIndex: 3);

        var bytes = serializer.SerializeToArray(original);
        var restored = serializer.Deserialize(bytes);

        Assert.NotNull(restored);
        Assert.NotSame(original, restored);
        Assert.Equal(HugeShardSequence, restored.ShardSequence);
        Assert.Equal(42, restored.SequenceNumber);
        Assert.Equal(3, restored.EventIndex);
        Assert.True(original.Equals(restored));
        Assert.Equal(0, original.CompareTo(restored));

        var newer = new KinesisSequenceToken(SlightlyLargerShardSequence, sequenceNumber: 0, eventIndex: 0);
        Assert.True(restored.CompareTo(newer) < 0);
    }

    [Fact]
    public void LegacyJsonDeserializationPreservesTokenFieldsAndOrdering()
    {
        var json = $$"""{"ShardSequence":"{{HugeShardSequence}}","SequenceNumber":7,"EventIndex":2}""";
        var restored = JsonConvert.DeserializeObject<KinesisSequenceToken>(json)!;

        Assert.NotNull(restored);
        Assert.Equal(HugeShardSequence, restored.ShardSequence);
        Assert.Equal(7, restored.SequenceNumber);
        Assert.Equal(2, restored.EventIndex);
        Assert.True(restored.CompareTo(new KinesisSequenceToken(SlightlyLargerShardSequence, 0, 0)) < 0);
    }
}
