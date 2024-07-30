using System.Collections.Immutable;
using System.Diagnostics;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

internal static class HashRingRangeExtensions
{
    internal static ImmutableArray<RingRange> HashRingAdditions(this ImmutableArray<RingRange> currentRanges, ImmutableArray<RingRange> previousRanges)
    {
        // Ranges in left must not overlap with each other.
        // Ranges in right must not overlap with each other.
        // Corresponding ranges in left and right have the same starting points.
        if (currentRanges.Length == previousRanges.Length)
        {
            var result = ImmutableArray.CreateBuilder<RingRange>(currentRanges.Length);
            for (var i = 0; i < currentRanges.Length; i++)
            {
                if (currentRanges.Length > previousRanges.Length)
                {
                    var c = currentRanges[i];
                    var p = previousRanges[i];
                    Debug.Assert(c.Start == p.Start);
                    result.Add(RingRange.Create(c.Start + p.Length, c.End));
                }
            }

            return result.ToImmutable();
        }
        else
        {
            if (currentRanges.Length > previousRanges.Length)
            {
                Debug.Assert(previousRanges.Length == 0);
                return currentRanges;
            }
            else
            {
                Debug.Assert(currentRanges.Length == 0 ^ previousRanges.Length == 0);
                return [];
            }
        }
    }

    internal static ImmutableArray<RingRange> HashRingRemovals(this ImmutableArray<RingRange> currentRanges, ImmutableArray<RingRange> previousRanges)
    {
        // Ranges in left must not overlap with each other.
        // Ranges in right must not overlap with each other.
        // Corresponding ranges in left and right have the same starting points.
        if (currentRanges.Length == previousRanges.Length)
        {
            Debug.Assert(currentRanges.Length == previousRanges.Length);
            var result = ImmutableArray.CreateBuilder<RingRange>(currentRanges.Length);
            for (var i = 0; i < currentRanges.Length; i++)
            {
                if (currentRanges.Length < previousRanges.Length)
                {
                    var c = currentRanges[i];
                    var p = previousRanges[i];
                    Debug.Assert(c.Start == p.Start);

                    result.Add(RingRange.Create(c.Start + c.Length, p.End));
                }
            }

            return result.ToImmutable();
        }
        else
        {
            if (previousRanges.Length > currentRanges.Length)
            {
                Debug.Assert(currentRanges.Length == 0);
                return previousRanges;
            }
            else
            {
                Debug.Assert(currentRanges.Length == 0 ^ previousRanges.Length == 0);
                return [];
            }
        }
    }

}