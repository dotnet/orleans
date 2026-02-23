using System.Collections.Immutable;
using Orleans.Runtime.GrainDirectory;
using CsCheck;
using Xunit;

namespace NonSilo.Tests.Directory;

/// <summary>
/// Tests for ring range collection operations including containment, intersection, and difference calculations.
/// </summary>
[TestCategory("BVT")]
public sealed class RingRangeCollectionTests
{
    private static readonly Gen<RingRangeCollection> GenRingRangeCollection = Gen.Int[0, 100].SelectMany(count => Gen.Select(Gen.UInt, Gen.Bool, static (boundary, included) => (boundary, included)).Array[count].Select(elements =>
    {
        var arr = ImmutableArray.CreateBuilder<RingRange>(elements.Length);
        for (var i = 1; i < arr.Count;)
        {
            var prev = elements[i - 1];
            var (boundary, included) = elements[i];
            if (!included)
            {
                continue;
            }

            arr.Add(RingRange.Create(prev.boundary, boundary));
        }

        return RingRangeCollection.Create(arr);
    }));

    [Fact]
    public void Contains()
    {
        Gen.Select(GenRingRangeCollection, Gen.UInt).Sample((ranges, point) =>
        {
            var doesContain = ranges.Ranges.Any(r => r.Contains(point));
            Assert.Equal(doesContain, ranges.Contains(point));
        });
    }

    [Fact]
    public void Intersects()
    {
        GenRingRangeCollection.Sample(ranges =>
        {
            foreach (var range in ranges.Ranges)
            {
                Assert.True(ranges.Intersects(range));
            }
        });
    }

    [Fact]
    public void Difference()
    {
        var ringWithUpdates = GenRingRangeCollection.SelectMany(original => Gen.Float[0f, 1f].Array[original.Ranges.Length].Select(diffs =>
        {
            // Increase or decrease the end of each range by some amount.
            var arr = ImmutableArray.CreateBuilder<RingRange>(original.Ranges.Length);
            for (var i = 0; i < diffs.Length; i++)
            {
                var orig = original.Ranges[i];
                var next = original.Ranges[(i + 1) % original.Ranges.Length];
                var maxPossibleLength = RingRange.Create(orig.Start, next.Start).Size;
                var newEnd = orig.Start + maxPossibleLength * diffs[i];
                arr.Add(RingRange.Create(orig.Start, (uint)Math.Clamp(orig.End + diffs[i], orig.Start + 1, next.Start)));
            }

            return (original, RingRangeCollection.Create(arr));
        }));

        ringWithUpdates.Sample((original, updated) =>
        {
            var additions = updated.Difference(original);
            
            foreach (var addition in additions)
            {
                Assert.True(updated.Intersects(addition));
                Assert.False(original.Intersects(addition));
            }

            var removals = updated.Difference(original);
            
            foreach (var removal in removals)
            {
                Assert.False(updated.Intersects(removal));
                Assert.True(original.Intersects(removal));
            }
        });
    }

    [Fact]
    public void ContainsTest()
    {
        Gen.Select(GenRingRangeCollection, Gen.UInt).Sample((collection, point) =>
        {
            var allRanges = collection.Ranges.ToList();
            var expectedContains = allRanges.Any(r => r.Contains(point));
            Assert.Equal(expectedContains, collection.Contains(point));
            var numContains = collection.Count(r => r.Contains(point));
            Assert.Equal(expectedContains ? 1 : 0, numContains);
        });
    }

    [Fact]
    public void ContainsWrappedTest()
    {
        var ranges = new RingRange[]
        {
            RingRange.Create(0x10930012, 0x179C5AD4),
            RingRange.Create(0x287844C7, 0x2B5DCCCB),
            RingRange.Create(0x32AC80C2, 0x36F72978),
            RingRange.Create(0x6F5C3AAC, 0x7776E202),
            RingRange.Create(0x7D2B02F3, 0x7DF52810),
            RingRange.Create(0xA18205D1, 0xA3A44031),
            RingRange.Create(0xA847CD39, 0xAD6C28D0),
            RingRange.Create(0xAF60D42F, 0xB278D2BE),
            RingRange.Create(0xBB8EA837, 0xC61DA5E1),
            RingRange.Create(0xF08C2237, 0xF3030A5A)
        }.ToImmutableArray();
        var collection = new RingRangeCollection(ranges);
        uint point = 0x16F4037C;
        Assert.True(ranges[0].Contains(point));
        Assert.True(collection.Contains(point));

        // Just outside the last range.
        point = 0xF3030A5A + 1;
        Assert.False(ranges[^1].Contains(point));
        Assert.False(collection.Contains(point));

        // Just inside the last range.
        point = 0xF3030A5A;
        Assert.True(ranges[^1].Contains(point));
        Assert.True(collection.Contains(point));

        // Between ranges.
        point = 0xF08C2237 - 1;
        Assert.False(collection.Contains(point));

        // In an interior range.
        point = 0x7D2B02F3 + 1;
        Assert.True(collection.Contains(point));
    }
}
