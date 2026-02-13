using Orleans.Runtime.GrainDirectory;
using CsCheck;
using Xunit;

namespace NonSilo.Tests.Directory;

/// <summary>
/// Tests for ring range operations including difference, complement, intersection, and containment logic.
/// </summary>
[TestCategory("BVT")]
public sealed class RingRangeTests
{
    internal static Gen<RingRange> GenRingRange => Gen.Select(Gen.UInt, Gen.UInt, RingRange.Create);

    [Fact]
    public void RingRangeDifference_EquallyDividedRange()
    {
        var previous = RingRange.Empty;
        var current = CreateEquallyDividedRange(2, 0);
        Assert.Empty(current.Difference(current));

        Assert.Equal(current, Assert.Single(current.Difference(previous)));
        Assert.Empty(previous.Difference(current));

        var firstHalf = CreateEquallyDividedRange(2, 0);
        var secondHalf = CreateEquallyDividedRange(2, 1);

        Assert.Equal(firstHalf, Assert.Single(firstHalf.Difference(secondHalf)));
        Assert.Equal(secondHalf, Assert.Single(secondHalf.Difference(firstHalf)));
    }

    [Fact]
    public void ComplementDoesNotIntersect()
    {
        GenRingRange.Where(range => !range.IsEmpty && !range.IsFull)
            .Sample((sample) =>
            {
                var inverse = sample.Complement();
                Assert.False(sample.Intersects(inverse));
                Assert.Empty(sample.Intersections(inverse));
                Assert.False(sample.Contains(inverse.End));
                var difference = Assert.Single(sample.Difference(inverse));
                Assert.Equal(sample, difference);
                var inverseDifference = Assert.Single(inverse.Difference(sample));
                Assert.Equal(inverse, inverseDifference);
            });
    }

    [Fact]
    public void ComplementComplementIsEqual()
    {
        GenRingRange
            .Sample((sample) =>
            {
                var inverse = sample.Complement();
                var inverseInverse = inverse.Complement();
                Assert.True(sample.Equals(inverseInverse));
            });
    }

    [Fact]
    public void RingRangeDifference_HolePunch()
    {
        var first = CreateEquallyDividedRange(8, 0);
        var second = CreateEquallyDividedRange(8, 1);
        var third = CreateEquallyDividedRange(8, 2);
        var fullRange = RingRange.Create(first.Start, third.End);

        var midPunch = fullRange.Difference(second);
        Assert.Equal(2, midPunch.Count());
        Assert.Equal(first, midPunch.First());
        Assert.Equal(third, midPunch.Last());
    }

    [Fact]
    public void RingRangeDifference_Empty()
    {
        var current = RingRange.Create(0x33333334, 0x66666667);
        var result = current.Difference(RingRange.Empty);
        Assert.Equal(current, Assert.Single(result));
    }

    [Fact]
    public void RingRangeDifference_Empty_Two()
    {
        var current = RingRange.Create(0x33333334, 0x66666667);
        var previous = RingRange.Create(uint.MaxValue - 1, 1);
        var result = Assert.Single(current.Difference(previous));
        Assert.Equal(current, result);
        Assert.Equal(previous, Assert.Single(previous.Difference(current)));
    }

    [Fact]
    public void RingRangeIntersection()
    {
        Assert.Empty(RingRange.Empty.Difference(RingRange.Empty));

        Assert.Empty(RingRange.Full.Difference(RingRange.Full));

        Assert.Equal(RingRange.Full, Assert.Single(RingRange.Full.Difference(RingRange.Empty)));

        Assert.Empty(RingRange.Empty.Difference(RingRange.Full));
    }

    [Fact]
    public void RingRangeContains()
    {
        Assert.False(RingRange.Empty.Contains(0));
        Assert.False(RingRange.Empty.Contains(1));
        Assert.False(RingRange.Empty.Contains(uint.MaxValue));
        Assert.False(RingRange.Empty.Contains(uint.MaxValue / 2));

        Assert.True(RingRange.Full.Contains(0));
        Assert.True(RingRange.Full.Contains(1));
        Assert.True(RingRange.Full.Contains(uint.MaxValue));
        Assert.True(RingRange.Full.Contains(uint.MaxValue / 2));

        var wrapped = RingRange.Create(uint.MaxValue - 10, 10);
        Assert.True(wrapped.Contains(0));
        Assert.True(wrapped.Contains(1));
        Assert.True(wrapped.Contains(uint.MaxValue));
        Assert.False(wrapped.Contains(uint.MaxValue / 2));
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
            var range = CreateEquallyDividedRange(count, i);
            Assert.False(previous.Intersects(range));
            sum += range.Size;
            previous = range;
        }

        Assert.Equal(uint.MaxValue, sum);
    }

    private static RingRange CreateEquallyDividedRange(int count, int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count, nameof(index));
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        return Core((uint)count, (uint)index);
        static RingRange Core(uint count, uint index)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count, nameof(index));

            if (count == 1 && index == 0)
            {
                return RingRange.Full;
            }

            var rangeSize = (ulong)uint.MaxValue + 1;
            var portion = rangeSize / count;
            var remainder = rangeSize - portion * count;
            var start = 0u;
            for (var i = 0; i < count; i++)
            {
                // (Start, End]
                var end = unchecked((uint)(start + portion));

                if (remainder > 0)
                {
                    end++;
                    remainder--;
                }

                if (i == index)
                {
                    return RingRange.Create(start, end);
                }

                start = end;
            }

            throw new ArgumentException(null, nameof(index));
        }
    }
}
