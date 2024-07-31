using Orleans.Runtime.GrainDirectory;
using Xunit;

namespace NonSilo.Tests.Directory;
public sealed class RingRangeTests
{
    [Fact]
    public void RingRangeAdditionsTest()
    {
        var previous = RingRange.Empty;
        var current = RingRange.CreateEquallyDividedRange(2, 0);
        Assert.Empty(current.GetAdditions(current));
        Assert.Empty(current.GetRemovals(current));

        Assert.Equal(current, Assert.Single(current.GetAdditions(previous)));
        Assert.Empty(current.GetRemovals(previous));

        var firstHalf = RingRange.CreateEquallyDividedRange(2, 0);
        var secondHalf = RingRange.CreateEquallyDividedRange(2, 1);

        Assert.Equal(firstHalf, Assert.Single(firstHalf.GetAdditions(secondHalf)));
        Assert.Equal(secondHalf, Assert.Single(firstHalf.GetRemovals(secondHalf)));
    }

    [Fact]
    public void RingRangeAdditionsTest_HolePunch()
    {
        var first = RingRange.CreateEquallyDividedRange(8, 0);
        var second = RingRange.CreateEquallyDividedRange(8, 1);
        var third = RingRange.CreateEquallyDividedRange(8, 2);
        var fullRange = RingRange.Create(first.Start, third.End);

        var midPunch = fullRange.GetAdditions(second);
        Assert.Equal(2, midPunch.Count());
        Assert.Equal(first, midPunch.First());
        Assert.Equal(third, midPunch.Last());
    }

    [Fact]
    public void RingRangeAdditionsTest_End()
    {
        var current = RingRange.Create(0x33333334, 0x66666667);
        var result = current.GetAdditions(RingRange.Empty);
        Assert.Equal(current, Assert.Single(result));
    }

    [Fact]
    public void RingRangeAdditionsTest_End_Two()
    {
        var current = RingRange.Create(0x33333334, 0x66666667);
        var result = current.GetAdditions(RingRange.Create(uint.MaxValue - 1, 1));
        Assert.Equal(current, Assert.Single(result));
    }

    [Fact]
    public void RingRangeIntersection()
    {
        Assert.Empty(RingRange.Empty.GetAdditions(RingRange.Empty));
        Assert.Empty(RingRange.Empty.GetRemovals(RingRange.Empty));

        Assert.Empty(RingRange.Full.GetAdditions(RingRange.Full));
        Assert.Empty(RingRange.Full.GetRemovals(RingRange.Full));

        Assert.Equal(RingRange.Full, Assert.Single(RingRange.Full.GetAdditions(RingRange.Empty)));

        Assert.Empty(RingRange.Empty.GetAdditions(RingRange.Full));
        Assert.Empty(RingRange.Full.GetRemovals(RingRange.Empty));
    }

    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(33)]
    [Theory]
    public void EqualRangeInvariants(int count)
    {
        var sum = 0ul;
        var previous = RingRange.Empty;
        for (var i = 0; i < count; i++)
        {
            var range = RingRange.CreateEquallyDividedRange(count, i);
            Assert.False(previous.Overlaps(range));
            sum += range.Size;
            previous = range;
        }

        Assert.Equal(uint.MaxValue, sum);
    }
}
