using System;
using Orleans.Providers.Streams.Common;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
public sealed class PartitionedStreamSequenceTokenTests
{
    [Theory]
    [InlineData("", "", 0)]
    [InlineData("", "000", 0)]
    [InlineData("0", "00000", 0)]
    [InlineData("00042", "42", 0)]
    [InlineData("0009", "10", -1)]
    [InlineData("00101", "100", 1)]
    [InlineData("170141183460469231731687303715884105727", "170141183460469231731687303715884105728", -1)]
    [InlineData("99999999999999999999999999999999999999", "100000000000000000000000000000000000000", -1)]
    [InlineData("001a", "01a", -1)]
    [InlineData("00A", "00a", -1)]
    [InlineData("0x10", "16", -1)]
    [InlineData("-2", "-10", 1)]
    [InlineData("12 ", "12", 1)]
    [InlineData("\u0661", "2", 1)]
    public void CompareTo_PreservesNumericAndOrdinalPositionOrdering(string leftPosition, string rightPosition, int expectedSign)
    {
        var left = new PartitionedStreamSequenceToken("provider", "partition", leftPosition, sequenceNumber: 1);
        var right = new PartitionedStreamSequenceToken("provider", "partition", rightPosition, sequenceNumber: 2);

        Assert.Equal(expectedSign, Math.Sign(left.CompareTo(right)));
        Assert.Equal(-expectedSign, Math.Sign(right.CompareTo(left)));
        Assert.Equal(expectedSign == 0, left.Equals(right));
        Assert.Equal(expectedSign == 0, right.Equals(left));
        if (expectedSign == 0)
        {
            Assert.Equal(left.GetHashCode(), right.GetHashCode());
        }
    }

    [Fact]
    public void CompareTo_UsesEventIndexAfterEquivalentNumericPositions()
    {
        var left = new PartitionedStreamSequenceToken("provider", "partition", "00042", sequenceNumber: 100, eventIndex: 1);
        var right = new PartitionedStreamSequenceToken("provider", "partition", "42", sequenceNumber: 1, eventIndex: 2);

        Assert.Equal(-1, Math.Sign(left.CompareTo(right)));
        Assert.Equal(1, Math.Sign(right.CompareTo(left)));
        Assert.False(left.Equals(right));
        Assert.False(right.Equals(left));
    }

    [Theory]
    [InlineData("000170141183460469231731687303715884105727", "00170141183460469231731687303715884105728", -1)]
    [InlineData("00042", "042", 0)]
    [InlineData("000record-a", "00record-b", -1)]
    public void CompareTo_DoesNotAllocateForPaddedPositions(string leftPosition, string rightPosition, int expectedSign)
    {
        var left = new PartitionedStreamSequenceToken("provider", "partition", leftPosition, sequenceNumber: 1);
        var right = new PartitionedStreamSequenceToken("provider", "partition", rightPosition, sequenceNumber: 2);
        Assert.Equal(expectedSign * 1_000, CompareRepeatedly(left, right));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = CompareRepeatedly(left, right);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(expectedSign * 1_000, result);
        Assert.Equal(0L, allocated);
    }

    private static int CompareRepeatedly(PartitionedStreamSequenceToken left, PartitionedStreamSequenceToken right)
    {
        var result = 0;
        for (var i = 0; i < 1_000; i++)
        {
            result += Math.Sign(left.CompareTo(right));
        }

        return result;
    }
}
