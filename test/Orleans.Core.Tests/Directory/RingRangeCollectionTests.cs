using System.Collections;
using System.Collections.Immutable;
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
        Assert.Equal(0u, empty.Size);
        Assert.Equal(0f, empty.SizePercent);

        Assert.False(full.IsEmpty);
        Assert.True(full.IsFull);
        Assert.Equal(uint.MaxValue, full.Size);
        Assert.Equal(100f, full.SizePercent);

        Assert.False(partial.IsEmpty);
        Assert.False(partial.IsFull);
        Assert.Equal(20u, partial.Size);
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

    private static RingRangeCollection Create(params RingRange[] ranges) => RingRangeCollection.Create(ranges);
}
