using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using CsCheck;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Xunit;

namespace NonSilo.Tests.Directory;

/// <summary>
/// Tests for ring range collection operations including containment, intersection, and difference calculations.
/// </summary>
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("GrainDirectory")]
public sealed class RingRangeCollectionTests
{
    [Fact]
    public void Create_SortsRangesAndOmitsEmptyRanges()
    {
        var low = RingRange.Create(10, 20);
        var high = RingRange.Create(30, 40);

        var collection = RingRangeCollection.Create(new[] { high, RingRange.Empty, low });

        Assert.Equal(new[] { low, high }, collection.Ranges);
    }

    [Fact]
    public void Properties_ReportEmptyFullAndPartialCollections()
    {
        var defaultCollection = default(RingRangeCollection);
        var empty = RingRangeCollection.Empty;
        var full = Create(RingRange.Full);
        var partial = Create(RingRange.Create(10, 20), RingRange.Create(30, 40));

        Assert.True(defaultCollection.IsDefault);

        Assert.False(empty.IsDefault);
        Assert.True(empty.IsEmpty);
        Assert.False(empty.IsFull);
        Assert.Equal(0UL, empty.Size);
        Assert.Equal(0f, empty.SizePercent);

        Assert.False(full.IsEmpty);
        Assert.True(full.IsFull);
        Assert.Equal(1UL << 32, full.Size);
        Assert.Equal(100f, full.SizePercent);

        Assert.False(partial.IsEmpty);
        Assert.False(partial.IsFull);
        Assert.Equal(20UL, partial.Size);
    }

    [Fact]
    public void Contains_UsesExclusiveStartsAndInclusiveEnds()
    {
        var collection = Create(RingRange.Create(10, 20), RingRange.Create(30, 40));

        Assert.False(collection.Contains(10));
        Assert.True(collection.Contains(11));
        Assert.True(collection.Contains(20));
        Assert.False(collection.Contains(21));
        Assert.False(collection.Contains(30));
        Assert.True(collection.Contains(40));
        Assert.False(collection.Contains(41));
    }

    [Fact]
    public void Contains_UsesTheGrainUniformHashCode()
    {
        var grainId = GrainId.Create("test", "grain");
        var collection = Create(RingRange.FromPoint(grainId.GetUniformHashCode()));

        Assert.True(collection.Contains(grainId));
        Assert.False(RingRangeCollection.Empty.Contains(grainId));
    }

    [Fact]
    public void Contains_HandlesWrappedRangeBoundaries()
    {
        var collection = Create(RingRange.Create(100, 10));

        Assert.False(collection.Contains(100));
        Assert.True(collection.Contains(101));
        Assert.True(collection.Contains(uint.MaxValue));
        Assert.True(collection.Contains(0));
        Assert.True(collection.Contains(10));
        Assert.False(collection.Contains(11));
        Assert.False(collection.Contains(50));
    }

    [Fact]
    public void IntersectsRange_HandlesEmptyContainedContainingAndDisjointRanges()
    {
        var collection = Create(RingRange.Create(10, 20), RingRange.Create(30, 40));

        Assert.False(RingRangeCollection.Empty.Intersects(RingRange.Create(10, 20)));
        Assert.False(collection.Intersects(RingRange.Empty));
        Assert.True(collection.Intersects(RingRange.Create(12, 18)));
        Assert.True(collection.Intersects(RingRange.Create(15, 25)));
        Assert.False(collection.Intersects(RingRange.Create(20, 30)));
        Assert.False(collection.Intersects(RingRange.Create(40, 50)));
    }

    [Fact]
    public void IntersectsRange_HandlesWrappedRangesAndBoundaryTouches()
    {
        var collection = Create(RingRange.Create(100, 10));

        Assert.True(collection.Intersects(RingRange.Create(0, 5)));
        Assert.True(collection.Intersects(RingRange.Create(150, 200)));
        Assert.False(collection.Intersects(RingRange.Create(10, 20)));
        Assert.False(collection.Intersects(RingRange.Create(90, 100)));
        Assert.False(collection.Intersects(RingRange.Create(20, 30)));
    }

    [Fact]
    public void IntersectsCollection_HandlesEitherContainmentDirectionAndEmptyCollections()
    {
        var inner = Create(RingRange.Create(15, 20));
        var overlapping = Create(RingRange.Create(10, 18));
        var outer = Create(RingRange.Create(10, 30));
        var disjoint = Create(RingRange.Create(30, 40));

        Assert.False(RingRangeCollection.Empty.Intersects(inner));
        Assert.False(inner.Intersects(RingRangeCollection.Empty));
        Assert.True(inner.Intersects(overlapping));
        Assert.True(outer.Intersects(inner));
        Assert.True(inner.Intersects(outer));
        Assert.False(inner.Intersects(disjoint));
        Assert.False(disjoint.Intersects(inner));
    }

    [Fact]
    public void Difference_ReturnsOnlyRangeGrowth()
    {
        var previous = Create(RingRange.Create(10, 20), RingRange.Create(30, 40));
        var current = Create(RingRange.Create(10, 25), RingRange.Create(30, 40));

        var result = current.Difference(previous);

        Assert.Equal(new[] { RingRange.Create(20, 25) }, result.Ranges);
        Assert.True(current.Intersects(result));
        Assert.False(previous.Intersects(result));
    }

    [Fact]
    public void Difference_ReturnsMissingPointWhenRangeBecomesFull()
    {
        var previous = Create(RingRange.Create(0, uint.MaxValue));
        var current = Create(RingRange.Full);

        var growth = current.Difference(previous);

        Assert.Equal(RingRange.FromPoint(0), Assert.Single(growth));
        Assert.Equal(1UL, growth.Size);
        Assert.True(previous.Difference(current).IsEmpty);
    }

    [Fact]
    public void Difference_PreservesSortOrderWhenWrappedRangeGrowthMovesToTheFront()
    {
        var previous = Create(RingRange.Create(10, 20), RingRange.Create(100, 5));
        var current = Create(RingRange.Create(10, 25), RingRange.Create(100, 8));

        var result = current.Difference(previous);

        Assert.Equal(
            new[] { RingRange.Create(5, 8), RingRange.Create(20, 25) },
            result.Ranges);
        Assert.All(result, addition => Assert.True(current.Intersects(addition)));
        Assert.All(result, addition => Assert.False(previous.Intersects(addition)));
    }

    [Fact]
    public void Difference_HandlesUnchangedAndEmptyCollections()
    {
        var collection = Create(RingRange.Create(10, 20), RingRange.Create(30, 40));

        Assert.True(collection.Difference(collection).IsEmpty);
        Assert.Equal(collection, collection.Difference(RingRangeCollection.Empty));
        Assert.True(RingRangeCollection.Empty.Difference(collection).IsEmpty);
        Assert.True(RingRangeCollection.Empty.Difference(RingRangeCollection.Empty).IsEmpty);
    }

    [Fact]
    public void Equality_UsesTheOrderedRangeSequenceAndTreatsEmptyCollectionsAsEqual()
    {
        var range = RingRange.Create(10, 20);
        var first = Create(range);
        var equal = Create(range);
        var different = Create(RingRange.Create(10, 21));
        var emptyWithExplicitRange = new RingRangeCollection(ImmutableArray.Create(RingRange.Empty));

        Assert.Equal(first, equal);
        Assert.True(first.Equals(equal));
        Assert.True(first == equal);
        Assert.False(first != equal);
        Assert.Equal(first.GetHashCode(), equal.GetHashCode());

        Assert.NotEqual(first, different);
        Assert.False(first.Equals(different));
        Assert.True(first != different);
        Assert.False(first.Equals(null));
        Assert.False(first.Equals("not a range collection"));

        Assert.Equal(RingRangeCollection.Empty, emptyWithExplicitRange);
        Assert.NotEqual(first, RingRangeCollection.Empty);
    }

    [Fact]
    public void Equality_EmptyRepresentationsShareHashCode()
    {
        RingRangeCollection[] collections =
        [
            default,
            RingRangeCollection.Empty,
            new(ImmutableArray.Create(RingRange.Empty))
        ];

        foreach (var collection in collections)
        {
            Assert.Equal(RingRangeCollection.Empty, collection);
            Assert.Equal(RingRangeCollection.Empty.GetHashCode(), collection.GetHashCode());
        }

        Assert.Single(new HashSet<RingRangeCollection>(collections));
    }

    [Fact]
    public void Enumeration_DefaultCollectionIsEmpty()
    {
        var collection = default(RingRangeCollection);
        var enumerator = collection.GetEnumerator();

        Assert.False(enumerator.MoveNext());
        Assert.Empty((IEnumerable<RingRange>)collection);
        Assert.Empty((IEnumerable)collection);
    }

    [Fact]
    public void Enumeration_ProducesEveryRangeForGenericAndNonGenericConsumers()
    {
        var expected = new[] { RingRange.Create(10, 20), RingRange.Create(30, 40) };
        var collection = Create(expected);

        Assert.Equal(expected, collection.ToArray());
        Assert.Equal(expected, ((IEnumerable)collection).Cast<RingRange>().ToArray());

        var enumerator = collection.GetEnumerator();
        Assert.True(enumerator.MoveNext());
        Assert.Equal(expected[0], enumerator.Current);

        var nonGenericEnumerator = ((IEnumerable)collection).GetEnumerator();
        Assert.True(nonGenericEnumerator.MoveNext());
        Assert.Equal(expected[0], nonGenericEnumerator.Current);
    }

    [Fact]
    public void Formatting_ReportsSubrangeCountAndSize()
    {
        var collection = Create(RingRange.Create(0, uint.MaxValue));
        Span<char> buffer = stackalloc char[64];

        Assert.True(((ISpanFormattable)collection).TryFormat(buffer, out var charsWritten, default, null));
        var formatted = buffer[..charsWritten].ToString();
        Assert.StartsWith("(1 subranges), ", formatted);
        Assert.EndsWith("%", formatted);
        Assert.Equal(formatted, collection.ToString());
        Assert.Equal(formatted, ((IFormattable)collection).ToString(null, null));

        Span<char> shortBuffer = stackalloc char[1];
        Assert.False(((ISpanFormattable)collection).TryFormat(shortBuffer, out charsWritten, default, null));
        Assert.Equal(0, charsWritten);
    }

    internal static Gen<RingRangeCollection> GenRingRangeCollection =>
        Gen.Select(Gen.UInt.Array[Gen.Int[0, 12]], Gen.Bool).Select(static (points, includeFull) =>
        {
            if (includeFull && points.Length == 0)
            {
                return Create(RingRange.Full);
            }

            Array.Sort(points);
            var distinctPoints = points.Distinct().ToArray();
            if (distinctPoints.Length < 2)
            {
                return RingRangeCollection.Empty;
            }

            var list = new List<RingRange>();
            for (int i = 0; i < distinctPoints.Length - 1; i += 2)
            {
                var r = RingRange.Create(distinctPoints[i], distinctPoints[i + 1]);
                if (!r.IsEmpty)
                {
                    list.Add(r);
                }
            }

            if (list.Count > 1 && list[0].Intersects(list[^1]))
            {
                list.RemoveAt(list.Count - 1);
            }

            return Create(list.ToArray());
        });

    [Fact]
    public void Create_NullArgument_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => RingRangeCollection.Create<List<RingRange>>(null!));
    }

    [Fact]
    public void BoundaryValueAnalysis_DefaultAndEmptyCollections()
    {
        var defaultCol = default(RingRangeCollection);
        Assert.True(defaultCol.IsDefault);
        Assert.True(defaultCol.IsEmpty);
        Assert.False(defaultCol.IsFull);
        Assert.Equal(0UL, defaultCol.Size);
        Assert.Equal(0.0f, defaultCol.SizePercent);
        Assert.False(defaultCol.Contains(0));
        Assert.False(defaultCol.Contains(uint.MaxValue));
        Assert.False(defaultCol.Intersects(RingRange.Full));
        Assert.False(defaultCol.Intersects(RingRangeCollection.Empty));
        Assert.True(defaultCol.Equals(RingRangeCollection.Empty));
        Assert.Equal(defaultCol.GetHashCode(), RingRangeCollection.Empty.GetHashCode());

        var emptyCol = RingRangeCollection.Empty;
        Assert.False(emptyCol.IsDefault);
        Assert.True(emptyCol.IsEmpty);
        Assert.False(emptyCol.IsFull);
        Assert.Equal(0UL, emptyCol.Size);
        Assert.Equal(0.0f, emptyCol.SizePercent);
        Assert.False(emptyCol.Contains(0));
        Assert.False(emptyCol.Intersects(RingRange.Full));
    }

    [Fact]
    public void BoundaryValueAnalysis_FullCollection()
    {
        var fullCol = Create(RingRange.Full);
        Assert.False(fullCol.IsDefault);
        Assert.False(fullCol.IsEmpty);
        Assert.True(fullCol.IsFull);
        Assert.Equal(1UL << 32, fullCol.Size);
        Assert.Equal(100.0f, fullCol.SizePercent);

        uint[] samplePoints = [0, 1, 2, uint.MaxValue - 1, uint.MaxValue];
        foreach (var p in samplePoints)
        {
            Assert.True(fullCol.Contains(p));
        }

        Assert.True(fullCol.Intersects(RingRange.Create(10, 20)));
        Assert.True(fullCol.Intersects(Create(RingRange.Create(10, 20))));
    }

    [Fact]
    public void BoundaryValueAnalysis_SizingAndOverflow()
    {
        // The almost-full range covers every point except 1.
        var almostFull = Create(RingRange.Create(1, 0));
        Assert.Equal((ulong)uint.MaxValue, almostFull.Size);
        Assert.False(almostFull.IsFull);

        // Together, the two non-overlapping ranges cover all 2^32 points.
        var half1 = RingRange.Create(0, 2_147_483_647u);
        var half2 = RingRange.Create(2_147_483_647u, 0);

        var fullCombined = Create(half1, half2);
        Assert.True(fullCombined.IsFull);
        Assert.Equal(1UL << 32, half1.Size + half2.Size);
        Assert.Equal(1UL << 32, fullCombined.Size);

        // A five-point gap reduces the covered point count by five.
        var withGap1 = RingRange.Create(0, 100);
        var withGap2 = RingRange.Create(105, 0);
        var collectionWithGap = Create(withGap1, withGap2);
        Assert.Equal((1UL << 32) - 5, collectionWithGap.Size);
        Assert.False(collectionWithGap.IsFull);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData((1u << 31) - 1)]
    [InlineData(1u << 31)]
    [InlineData(uint.MaxValue - 1)]
    [InlineData(uint.MaxValue)]
    public void Size_CountsComplementaryRangesAtBoundaries(uint point)
    {
        var singlePoint = RingRange.FromPoint(point);
        var almostFull = Create(singlePoint.Complement());
        var full = Create(singlePoint, singlePoint.Complement());

        Assert.Equal(1UL, Create(singlePoint).Size);
        Assert.Equal((1UL << 32) - 1, almostFull.Size);
        Assert.False(almostFull.IsFull);
        Assert.False(almostFull.Contains(point));
        Assert.Equal(1UL << 32, full.Size);
        Assert.True(full.IsFull);
        Assert.True(full.Contains(point));
        Assert.Equal(100f, full.SizePercent);
    }

    [Fact]
    public void BoundaryValueAnalysis_DifferenceScenarios()
    {
        var c1 = Create(RingRange.Create(10, 20));
        var c2 = Create(RingRange.Create(10, 20), RingRange.Create(30, 40));

        // Difference when previous has fewer ranges (0 vs 1)
        Assert.Equal(c1, c1.Difference(RingRangeCollection.Empty));

        // Difference when current is empty vs non-empty previous (0 vs 1)
        Assert.True(RingRangeCollection.Empty.Difference(c1).IsEmpty);
    }

    [Fact]
    public void Property_Contains_MatchesIndividualRanges()
    {
        Gen.Select(GenRingRangeCollection, Gen.UInt).Sample((collection, point) =>
        {
            var expected = collection.Ranges.IsDefaultOrEmpty ? false : collection.Ranges.Any(r => r.Contains(point));
            Assert.Equal(expected, collection.Contains(point));
        });
    }

    [Fact]
    public void Property_IntersectsRange_MatchesIndividualRanges()
    {
        Gen.Select(GenRingRangeCollection, RingRangeTests.GenRingRange).Sample((collection, range) =>
        {
            var actual = collection.Intersects(range);
            if (collection.IsEmpty || range.IsEmpty)
            {
                Assert.False(actual);
            }
            else
            {
                var expected = collection.Contains(range.End) || collection.Ranges.Any(r => range.Contains(r.End));
                Assert.Equal(expected, actual);
            }
        });
    }

    [Fact]
    public void Property_IntersectsCollection_SymmetricAndMatchesRanges()
    {
        Gen.Select(GenRingRangeCollection, GenRingRangeCollection).Sample((c1, c2) =>
        {
            var actual1 = c1.Intersects(c2);
            var actual2 = c2.Intersects(c1);

            Assert.Equal(actual1, actual2);

            if (c1.IsEmpty || c2.IsEmpty)
            {
                Assert.False(actual1);
            }
            else
            {
                var expected = c1.Ranges.Any(r1 => c2.Ranges.Any(r2 => r1.Intersects(r2)));
                Assert.Equal(expected, actual1);
            }
        });
    }

    [Fact]
    public void Property_SizeAndIsFull()
    {
        GenRingRangeCollection.Sample(collection =>
        {
            var expectedSize = collection.Ranges.Aggregate(0UL, static (sum, range) => sum + RingRangeTests.GetExpectedSize(range));

            Assert.InRange(expectedSize, 0UL, 1UL << 32);
            Assert.Equal(expectedSize, collection.Size);
            Assert.Equal(expectedSize == 1UL << 32, collection.IsFull);
            Assert.Equal(expectedSize * (100.0f / (1UL << 32)), collection.SizePercent);
        });
    }

    [Fact]
    public void Property_SizeCountsFullPartitionsAndSinglePointGaps()
    {
        Gen.Select(RingRangeTests.GenBoundaryPoint.Array[Gen.Int[0, 12]], RingRangeTests.GenBoundaryPoint).Sample((points, missingPoint) =>
        {
            var boundaries = points.Append(0u).Distinct().Order().ToArray();
            var ranges = boundaries.Length == 1
                ? new[] { RingRange.Full }
                : boundaries.Select((start, index) => RingRange.Create(start, boundaries[(index + 1) % boundaries.Length])).ToArray();
            var full = Create(ranges);

            Assert.Equal(1UL << 32, ranges.Aggregate(0UL, static (sum, range) => sum + range.Size));
            Assert.Equal(1UL << 32, full.Size);
            Assert.True(full.IsFull);
            Assert.True(full.Contains(missingPoint));

            var gap = RingRange.FromPoint(missingPoint);
            var almostFull = Create(ranges.SelectMany(range => range.Difference(gap)).ToArray());

            Assert.Equal((1UL << 32) - 1, almostFull.Size);
            Assert.False(almostFull.IsFull);
            Assert.False(almostFull.Contains(missingPoint));
            Assert.Equal(1UL, full.Size - almostFull.Size);
        });
    }

    [Fact]
    public void Property_EqualityAndHashing()
    {
        Gen.Select(GenRingRangeCollection, GenRingRangeCollection, GenRingRangeCollection).Sample((c1, c2, c3) =>
        {
            var c1Same = c1;
            // Reflexivity
            Assert.True(c1 == c1Same);
            Assert.True(c1.Equals(c1));
            Assert.True(c1.Equals((object)c1));
            Assert.False(c1 != c1Same);

            // Symmetry
            Assert.Equal(c1 == c2, c2 == c1);
            Assert.Equal(c1.Equals(c2), c2.Equals(c1));

            // Transitivity
            if (c1 == c2 && c2 == c3)
            {
                Assert.True(c1 == c3);
            }

            // HashCode consistency
            if (c1 == c2)
            {
                Assert.Equal(c1.GetHashCode(), c2.GetHashCode());
            }

            Assert.Equal(c1 != c2, !(c1 == c2));
        });
    }

    private static RingRangeCollection Create(params RingRange[] ranges) => RingRangeCollection.Create(ranges);
}
