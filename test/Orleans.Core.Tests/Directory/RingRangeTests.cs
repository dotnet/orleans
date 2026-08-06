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
    public void RingRangeIntersectionsMatchIndependentModel()
    {
        Gen.Select(GenRingRange, GenRingRange).Sample(AssertIntersectionsMatchIndependentModel);
    }

    [Fact]
    public void RingRangeIntersectionsMatchIndependentModel_WhenWrappedRangeContainsNormalRange()
    {
        var lowSideCases = Gen.Select(Gen.Int[0, 10_000], Gen.Int[0, 10_000], Gen.Int[0, 10_000], static (wrappedEndSeed, normalStartSeed, normalLengthSeed) =>
        {
            var wrappedEnd = (uint)(2 + wrappedEndSeed);
            var normalStart = (uint)(normalStartSeed % (int)(wrappedEnd - 1));
            var normalEnd = normalStart + 1 + (uint)(normalLengthSeed % (int)(wrappedEnd - normalStart));
            return (Wrapped: RingRange.Create(uint.MaxValue - 10_000, wrappedEnd), Normal: RingRange.Create(normalStart, normalEnd));
        });

        var highSideCases = Gen.Select(Gen.Int[0, 10_000], Gen.Int[0, 10_000], Gen.Int[0, 10_000], static (wrappedStartSeed, normalStartOffsetSeed, normalLengthSeed) =>
        {
            var wrappedStart = (uint)(wrappedStartSeed + 1);
            var normalStart = wrappedStart + 1 + (uint)normalStartOffsetSeed;
            var normalEnd = normalStart + 1 + (uint)normalLengthSeed;
            return (Wrapped: RingRange.Create(wrappedStart, 0), Normal: RingRange.Create(normalStart, normalEnd));
        });

        lowSideCases.Sample(testCase => AssertIntersectionsMatchIndependentModel(testCase.Wrapped, testCase.Normal));
        highSideCases.Sample(testCase => AssertIntersectionsMatchIndependentModel(testCase.Wrapped, testCase.Normal));
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

    private static void AssertIntersectionsMatchIndependentModel(RingRange left, RingRange right)
    {
        var expected = GetExpectedIntersections(left, right).ToArray();

        var actual = left.Intersections(right).ToArray();
        Assert.Equal(expected, actual);
        Assert.All(actual, intersection =>
        {
            Assert.True(Contains(left, intersection));
            Assert.True(Contains(right, intersection));
        });

        var reversed = right.Intersections(left).ToArray();
        Assert.Equal(expected, reversed);
        Assert.Equal(expected.Length > 0, left.Intersects(right));
        Assert.Equal(left.Intersects(right), right.Intersects(left));
    }

    private static IEnumerable<RingRange> GetExpectedIntersections(RingRange left, RingRange right)
    {
        var intervals = new List<(uint Start, uint End)>();
        foreach (var leftInterval in ToIntervals(left))
        {
            foreach (var rightInterval in ToIntervals(right))
            {
                var start = Math.Max(leftInterval.Start, rightInterval.Start);
                var end = Math.Min(leftInterval.End, rightInterval.End);
                if (start <= end)
                {
                    intervals.Add((start, end));
                }
            }
        }

        intervals.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        if (intervals.Count >= 2 && intervals[0].Start == 0 && intervals[^1].End == uint.MaxValue)
        {
            yield return RingRange.Create(unchecked(intervals[^1].Start - 1), intervals[0].End);
            for (var i = 1; i < intervals.Count - 1; i++)
            {
                yield return FromInclusiveInterval(intervals[i].Start, intervals[i].End);
            }

            yield break;
        }

        foreach (var interval in intervals)
        {
            yield return FromInclusiveInterval(interval.Start, interval.End);
        }
    }

    private static bool Contains(RingRange range, RingRange candidate)
    {
        return ToIntervals(candidate).All(candidateInterval =>
            ToIntervals(range).Any(rangeInterval => rangeInterval.Start <= candidateInterval.Start && candidateInterval.End <= rangeInterval.End));
    }

    private static IEnumerable<(uint Start, uint End)> ToIntervals(RingRange range)
    {
        if (range.IsEmpty)
        {
            yield break;
        }

        if (range.IsFull)
        {
            yield return (0, uint.MaxValue);
            yield break;
        }

        if (range.Start < range.End)
        {
            yield return (range.Start + 1, range.End);
            yield break;
        }

        yield return (0, range.End);
        if (range.Start < uint.MaxValue)
        {
            yield return (range.Start + 1, uint.MaxValue);
        }
    }

    private static RingRange FromInclusiveInterval(uint start, uint end)
    {
        if (start == 0 && end == uint.MaxValue)
        {
            return RingRange.Full;
        }

        return RingRange.Create(unchecked(start - 1), end);
    }
}
