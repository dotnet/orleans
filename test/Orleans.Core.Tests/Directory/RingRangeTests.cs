using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using CsCheck;
using Xunit;

namespace NonSilo.Tests.Directory;

/// <summary>
/// Tests for ring range operations including difference, complement, intersection, and containment logic.
/// </summary>
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("GrainDirectory")]
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

        var expectedSum = count == 1 ? uint.MaxValue : (ulong)uint.MaxValue + 1;
        Assert.Equal(expectedSum, sum);
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

    [Fact]
    public void FromPoint_UnitTests()
    {
        // Point 0 (wraps around: Start = uint.MaxValue, End = 0)
        var range0 = RingRange.FromPoint(0);
        Assert.Equal(uint.MaxValue, range0.Start);
        Assert.Equal(0u, range0.End);
        Assert.Equal(1u, range0.Size);
        Assert.False(range0.IsEmpty);
        Assert.False(range0.IsFull);
        Assert.True(range0.IsWrapped);
        Assert.True(range0.Contains(0));
        Assert.False(range0.Contains(1));
        Assert.False(range0.Contains(uint.MaxValue));

        // Point 1 (Start = 0, End = 1)
        var range1 = RingRange.FromPoint(1);
        Assert.Equal(0u, range1.Start);
        Assert.Equal(1u, range1.End);
        Assert.Equal(1u, range1.Size);
        Assert.False(range1.IsEmpty);
        Assert.False(range1.IsFull);
        Assert.False(range1.IsWrapped);
        Assert.True(range1.Contains(1));
        Assert.False(range1.Contains(0));
        Assert.False(range1.Contains(2));

        // Arbitrary point 42 (Start = 41, End = 42)
        var range42 = RingRange.FromPoint(42);
        Assert.Equal(41u, range42.Start);
        Assert.Equal(42u, range42.End);
        Assert.Equal(1u, range42.Size);
        Assert.True(range42.Contains(42));
        Assert.False(range42.Contains(41));
        Assert.False(range42.Contains(43));

        // Point uint.MaxValue (Start = uint.MaxValue - 1, End = uint.MaxValue)
        var rangeMax = RingRange.FromPoint(uint.MaxValue);
        Assert.Equal(uint.MaxValue - 1, rangeMax.Start);
        Assert.Equal(uint.MaxValue, rangeMax.End);
        Assert.Equal(1u, rangeMax.Size);
        Assert.True(rangeMax.Contains(uint.MaxValue));
        Assert.False(rangeMax.Contains(uint.MaxValue - 1));
        Assert.False(rangeMax.Contains(0));
    }

    [Fact]
    public void FromPoint_GrainId()
    {
        var grainId = GrainId.Create("testGrainType", "testGrainKey");
        var range = RingRange.FromPoint(grainId.GetUniformHashCode());
        Assert.True(range.Contains(grainId));
    }

    [Fact]
    public void FromPoint_CsCheckProperty()
    {
        Gen.Select(Gen.UInt, Gen.UInt).Sample((point, other) =>
        {
            var range = RingRange.FromPoint(point);
            Assert.Equal(1u, range.Size);
            Assert.False(range.IsEmpty);
            Assert.False(range.IsFull);
            Assert.True(range.Contains(point));
            Assert.Equal(unchecked(point - 1), range.Start);
            Assert.Equal(point, range.End);
            Assert.Equal(point == 0, range.IsWrapped);

            Assert.Equal(other == point, range.Contains(other));
            Assert.Equal(0, range.CompareTo(point));

            var complement = range.Complement();
            Assert.False(complement.Contains(point));
            Assert.Equal(uint.MaxValue, complement.Size);
        });
    }

    [Fact]
    public void RingRange_CreateAndNormalization()
    {
        var empty = RingRange.Create(0, 0);
        Assert.True(empty.IsEmpty);
        Assert.False(empty.IsFull);
        Assert.Equal(0u, empty.Start);
        Assert.Equal(0u, empty.End);

        var full1 = RingRange.Create(1, 1);
        Assert.False(full1.IsEmpty);
        Assert.True(full1.IsFull);
        Assert.Equal(0u, full1.Start);
        Assert.Equal(0u, full1.End);

        var full2 = RingRange.Create(2, 2);
        Assert.False(full2.IsEmpty);
        Assert.True(full2.IsFull);
        Assert.Equal(0u, full2.Start);
        Assert.Equal(0u, full2.End);

        var fullMax = RingRange.Create(uint.MaxValue, uint.MaxValue);
        Assert.False(fullMax.IsEmpty);
        Assert.True(fullMax.IsFull);

        Assert.True(RingRange.Empty.IsEmpty);
        Assert.True(RingRange.Full.IsFull);
    }

    [Fact]
    public void RingRange_SizeAndPercent_CsCheckProperty()
    {
        Gen.Select(Gen.UInt, Gen.UInt).Sample((start, end) =>
        {
            var range = RingRange.Create(start, end);
            ulong expectedSize;
            if (start == end)
            {
                expectedSize = start == 0 ? 0UL : uint.MaxValue;
            }
            else if (start < end)
            {
                expectedSize = (ulong)end - start;
            }
            else
            {
                expectedSize = (ulong)uint.MaxValue - start + end + 1;
            }

            Assert.Equal((uint)expectedSize, range.Size);
            Assert.Equal((float)(expectedSize * (100.0f / uint.MaxValue)), range.SizePercent);
            Assert.Equal(start == 0 && end == 0, range.IsEmpty);
            Assert.Equal(start == end && start != 0, range.IsFull);
            Assert.Equal(start >= end && start != 0, range.IsWrapped);
        });
    }

    [Fact]
    public void RingRange_Contains_CsCheckProperty()
    {
        Gen.Select(GenRingRange, Gen.UInt).Sample((range, point) =>
        {
            bool expected;
            if (range.IsEmpty)
            {
                expected = false;
            }
            else if (range.IsFull)
            {
                expected = true;
            }
            else if (range.Start < range.End)
            {
                expected = point > range.Start && point <= range.End;
            }
            else
            {
                expected = point > range.Start || point <= range.End;
            }

            Assert.Equal(expected, range.Contains(point));
        });
    }

    [Fact]
    public void RingRange_CompareTo_UnitTests()
    {
        // Full range
        Assert.Equal(0, RingRange.Full.CompareTo(0));
        Assert.Equal(0, RingRange.Full.CompareTo(42));
        Assert.Equal(0, RingRange.Full.CompareTo(uint.MaxValue));

        // Empty range
        Assert.Equal(1, RingRange.Empty.CompareTo(0));
        Assert.Equal(-1, RingRange.Empty.CompareTo(1));
        Assert.Equal(-1, RingRange.Empty.CompareTo(100));

        // Normal range (10, 20]
        var normal = RingRange.Create(10, 20);
        Assert.Equal(0, normal.CompareTo(11));
        Assert.Equal(0, normal.CompareTo(15));
        Assert.Equal(0, normal.CompareTo(20));
        Assert.Equal(1, normal.CompareTo(10));
        Assert.Equal(1, normal.CompareTo(5));
        Assert.Equal(-1, normal.CompareTo(21));
        Assert.Equal(-1, normal.CompareTo(100));

        // Wrapped range (100, 10]
        var wrapped = RingRange.Create(100, 10);
        Assert.Equal(0, wrapped.CompareTo(0));
        Assert.Equal(0, wrapped.CompareTo(5));
        Assert.Equal(0, wrapped.CompareTo(10));
        Assert.Equal(0, wrapped.CompareTo(101));
        Assert.Equal(0, wrapped.CompareTo(uint.MaxValue));
        Assert.Equal(-1, wrapped.CompareTo(11));
        Assert.Equal(-1, wrapped.CompareTo(50));
        Assert.Equal(-1, wrapped.CompareTo(100));
    }

    [Fact]
    public void RingRange_CompareTo_CsCheckProperty()
    {
        Gen.Select(GenRingRange, Gen.UInt).Sample((range, point) =>
        {
            var cmp = range.CompareTo(point);
            if (range.Contains(point))
            {
                Assert.Equal(0, cmp);
            }
            else
            {
                Assert.NotEqual(0, cmp);
                if (range.IsWrapped)
                {
                    if (point <= range.Start)
                    {
                        Assert.Equal(-1, cmp);
                    }
                    else
                    {
                        Assert.Equal(1, cmp);
                    }
                }
                else
                {
                    if (point <= range.Start)
                    {
                        Assert.Equal(1, cmp);
                    }
                    else
                    {
                        Assert.Equal(-1, cmp);
                    }
                }
            }
        });
    }

    [Fact]
    public void RingRange_EqualityAndHashing_UnitTests()
    {
        var r1 = RingRange.Create(10, 20);
        var r1Copy = RingRange.Create(10, 20);
        var r2 = RingRange.Create(10, 21);

        Assert.True(r1.Equals(r1Copy));
        Assert.True(r1.Equals((object)r1Copy));
        Assert.False(r1.Equals(r2));
        Assert.False(r1.Equals(null));
        Assert.False(r1.Equals("not a range"));

        Assert.True(r1 == r1Copy);
        Assert.False(r1 != r1Copy);
        Assert.False(r1 == r2);
        Assert.True(r1 != r2);

        Assert.Equal(r1.GetHashCode(), r1Copy.GetHashCode());
    }

    [Fact]
    public void RingRange_EqualityAndHashing_CsCheckProperty()
    {
        Gen.Select(GenRingRange, GenRingRange, GenRingRange).Sample((r1, r2, r3) =>
        {
            var r1Same = r1;
            // Reflexivity
            Assert.True(r1 == r1Same);
            Assert.True(r1.Equals(r1));
            Assert.True(r1.Equals((object)r1));
            Assert.False(r1 != r1Same);

            // Symmetry
            Assert.Equal(r1 == r2, r2 == r1);
            Assert.Equal(r1.Equals(r2), r2.Equals(r1));

            // Transitivity
            if (r1 == r2 && r2 == r3)
            {
                Assert.True(r1 == r3);
            }

            // HashCode consistency
            if (r1 == r2)
            {
                Assert.Equal(r1.GetHashCode(), r2.GetHashCode());
            }

            Assert.Equal(r1 != r2, !(r1 == r2));
        });
    }

    [Fact]
    public void RingRange_Formatting_UnitTests()
    {
        Assert.Equal("(0, 0) 0.00%", RingRange.Empty.ToString());
        Assert.Equal("(0, 0] (100.00%)", RingRange.Full.ToString());

        var range = RingRange.Create(1, 10);
        var formatted = range.ToString();
        Assert.Contains("0x00000001", formatted);
        Assert.Contains("0x0000000A", formatted);

        Assert.Equal(formatted, ((IFormattable)range).ToString(null, null));

        Span<char> buffer = stackalloc char[64];
        Assert.True(((ISpanFormattable)range).TryFormat(buffer, out var charsWritten, default, null));
        Assert.Equal(formatted, buffer[..charsWritten].ToString());

        Span<char> smallBuffer = stackalloc char[1];
        Assert.False(((ISpanFormattable)range).TryFormat(smallBuffer, out charsWritten, default, null));
        Assert.Equal(0, charsWritten);
    }

    [Fact]
    public void RingRange_Complement_CsCheckProperty()
    {
        Gen.Select(GenRingRange, Gen.UInt).Sample((r, p) =>
        {
            var comp = r.Complement();
            Assert.Equal(r, comp.Complement());

            if (!r.IsEmpty && !r.IsFull)
            {
                Assert.True(r.Contains(p) ^ comp.Contains(p));
                Assert.Equal((ulong)uint.MaxValue + 1, (ulong)r.Size + comp.Size);
                Assert.False(r.Intersects(comp));
            }
        });
    }
}
