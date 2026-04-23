#nullable enable
using System.Linq;
using Orleans.Runtime.GrainDirectory;
using TestExtensions;
using Xunit;

namespace UnitTests.GrainDirectory;

[TestCategory("BVT"), TestCategory("Directory")]
public sealed class GrainDirectoryPartitionBatchingTests
{
    [Fact]
    public void GetActivationQueryRanges_ReturnsNoRangesForEmptyRange()
    {
        var ranges = GrainDirectoryPartition.GetActivationQueryRanges(RingRange.Empty).ToArray();

        Assert.Empty(ranges);
    }

    [Fact]
    public void GetActivationQueryRanges_ReturnsOriginalRangeForSmallRange()
    {
        var range = RingRange.Create(100, 200);

        var ranges = GrainDirectoryPartition.GetActivationQueryRanges(range).ToArray();

        var result = Assert.Single(ranges);
        Assert.Equal(range, result);
    }

    [Fact]
    public void GetActivationQueryRanges_SplitsLargeRangeIntoContiguousBalancedRanges()
    {
        var range = RingRange.Create(0x1000, 0x30001000);

        var ranges = GrainDirectoryPartition.GetActivationQueryRanges(range).ToArray();

        Assert.True(ranges.Length > 1);
        AssertRangePartition(range, ranges);
        AssertBalancedRanges(ranges);
    }

    [Fact]
    public void GetActivationQueryRanges_SplitsWrappedRangeIntoContiguousBalancedRanges()
    {
        var range = RingRange.Create(0xD0000000, 0x20000000);

        var ranges = GrainDirectoryPartition.GetActivationQueryRanges(range).ToArray();

        Assert.True(ranges.Length > 1);
        AssertRangePartition(range, ranges);
        AssertBalancedRanges(ranges);
    }

    [Fact]
    public void GetActivationQueryRanges_SplitsFullRangeAcrossEntireRing()
    {
        var ranges = GrainDirectoryPartition.GetActivationQueryRanges(RingRange.Full).ToArray();

        Assert.True(ranges.Length > 1);
        AssertRangePartition(RingRange.Full, ranges);
        AssertBalancedRanges(ranges);
    }

    private static void AssertRangePartition(RingRange range, RingRange[] ranges)
    {
        Assert.NotEmpty(ranges);
        Assert.All(ranges, static candidate => Assert.False(candidate.IsEmpty));
        Assert.Equal(range.Start, ranges[0].Start);
        Assert.Equal(range.End, ranges[^1].End);
        Assert.Equal(GetRangeSize(range), ranges.Aggregate(0UL, static (sum, range) => sum + GetRangeSize(range)));

        for (var i = 1; i < ranges.Length; i++)
        {
            Assert.Equal(ranges[i - 1].End, ranges[i].Start);
        }
    }

    private static void AssertBalancedRanges(RingRange[] ranges)
    {
        var sizes = ranges.Select(GetRangeSize).ToArray();

        Assert.True(sizes.Max() - sizes.Min() <= 1);
    }

    private static ulong GetRangeSize(RingRange range)
    {
        if (range.IsFull)
        {
            return (ulong)uint.MaxValue + 1;
        }

        return range.IsWrapped
            ? (ulong)uint.MaxValue - range.Start + range.End + 1
            : range.Size;
    }
}
