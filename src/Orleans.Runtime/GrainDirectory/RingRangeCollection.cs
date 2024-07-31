using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Orleans.Runtime.Utilities;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

// Read-only, sorted collection of non-overlapping ranges.
[GenerateSerializer, Alias(nameof(RingRangeCollection))]
internal readonly struct RingRangeCollection : IEquatable<RingRangeCollection>, ISpanFormattable
{
    public RingRangeCollection(ImmutableArray<RingRange> ranges)
    {
        Debug.Assert(!ranges.IsDefault);

#if DEBUG
        // Ranges must be in sorted order and must not overlap with each other.
        for (var i = 1; i < ranges.Length; i++)
        {
            var prev = ranges[i - 1];
            var curr = ranges[i];
            Debug.Assert(!prev.Overlaps(curr));
            Debug.Assert(curr.Start >= prev.Start);
        }

        if (ranges.Length > 1)
        {
            Debug.Assert(!ranges[0].Overlaps(ranges[^1]));
        }
#endif

        Ranges = ranges;
    }

    public static RingRangeCollection Empty { get; } = new([]);

    [Id(0)]
    public ImmutableArray<RingRange> Ranges { get; }

    public bool IsDefault => Ranges.IsDefault;

    public bool IsEmpty => Ranges.Length == 0 || Ranges.All(r => r.IsEmpty);

    public bool IsFull => !IsEmpty && Ranges.Sum(r => r.Size) == uint.MaxValue;

    public float SizePercent => Ranges.Sum(static r => r.SizePercent);

    public bool Contains(GrainId grainId) => Contains(grainId.GetUniformHashCode());

    public bool Contains(uint value)
    {
        // Binary search with wrap-around to include the last element to handle the case
        // where it wraps around the boundary of the ring.
        return Ranges.Length > 0
            && SearchAlgorithms.BinarySearch(
                Ranges.Length + 1,
                (this, value),
                static (index, state) =>
                {
                    var (virtualRange, hashCode) = state;
                    if (index == 0)
                    {
                        index = virtualRange.Ranges.Length;
                    }

                    return virtualRange.Ranges[index - 1].CompareTo(hashCode);
                }) >= 0;
    }

    public bool Overlaps(RingRange other)
    {
        if (IsEmpty || other.IsEmpty)
        {
            return false;
        }

        if (Contains(other.End))
        {
            return true;
        }

        foreach (var range in Ranges)
        {
            if (other.Contains(range.End))
            {
                return true;
            }
        }

        return false;
    }

    public bool Overlaps(RingRangeCollection other)
    {
        if (IsEmpty || other.IsEmpty)
        {
            return false;
        }

        foreach (var range in Ranges)
        {
            if (other.Contains(range.End))
            {
                return true;
            }
        }

        foreach (var otherRange in other.Ranges)
        {
            if (Contains(otherRange.End))
            {
                return true;
            }
        }

        return false;
    }

    public RingRangeCollection GetAdditions(RingRangeCollection previous)
    {
        // Ranges in left must not overlap with each other.
        // Ranges in right must not overlap with each other.
        // Corresponding ranges in left and right have the same starting points.
        // The number of ranges in both 'Ranges' or 'previous.Ranges' is either zero or the configured number of ranges,
        // i.e., if both collections have more than zero ranges, the both have the same number of ranges.
        if (Ranges.Length == previous.Ranges.Length)
        {
            var result = ImmutableArray.CreateBuilder<RingRange>(Ranges.Length);
            for (var i = 0; i < Ranges.Length; i++)
            {
                var c = Ranges[i];
                var p = previous.Ranges[i];
                Debug.Assert(c.Start == p.Start);
                if (c.Size > p.Size)
                {
                    result.Add(RingRange.Create(p.End, c.End));
                }
            }

            // If the last range wrapped around but its extension does not wrap around, move it to the front.
            // This preserves sort order.
            if (result.Count > 1 && result[^1].Start < result[^2].Start)
            {
                var last = result[^1];
                result.RemoveAt(result.Count - 1);
                result.Insert(0, last);
            }

            return new(result.ToImmutable());
        }
        else
        {
            if (Ranges.Length > previous.Ranges.Length)
            {
                Debug.Assert(previous.Ranges.Length == 0);
                return this;
            }
            else
            {
                Debug.Assert(Ranges.Length == 0 ^ previous.Ranges.Length == 0);
                return Empty;
            }
        }
    }

    internal RingRangeCollection GetRemovals(RingRangeCollection previous)
    {
        // Ranges in left must not overlap with each other.
        // Ranges in right must not overlap with each other.
        // Corresponding ranges in left and right have the same starting points.
        // The number of ranges in both 'Ranges' or 'previous.Ranges' is either zero or the configured number of ranges,
        // i.e., if both collections have more than zero ranges, the both have the same number of ranges.
        if (Ranges.Length == previous.Ranges.Length)
        {
            Debug.Assert(Ranges.Length == previous.Ranges.Length);
            var result = ImmutableArray.CreateBuilder<RingRange>(Ranges.Length);
            for (var i = 0; i < Ranges.Length; i++)
            {
                var c = Ranges[i];
                var p = previous.Ranges[i];
                Debug.Assert(c.Start == p.Start);
                if (c.Size < p.Size)
                {
                    result.Add(RingRange.Create(c.End, p.End));
                }
            }

            // If the last range wrapped around but its truncation does not wrap around, move it to the front.
            // This preserves sort order.
            if (result.Count > 1 && result[^1].Start < result[^2].Start)
            {
                var last = result[^1];
                result.RemoveAt(result.Count - 1);
                result.Insert(0, last);
            }

            return new(result.ToImmutable());
        }
        else
        {
            if (previous.Ranges.Length > Ranges.Length)
            {
                Debug.Assert(Ranges.Length == 0);
                return previous;
            }
            else
            {
                Debug.Assert(Ranges.Length == 0 ^ previous.Ranges.Length == 0);
                return Empty;
            }
        }
    }

    public bool Equals(RingRangeCollection other)
    {
        if (IsEmpty && other.IsEmpty)
        {
            return true;
        }

        if (IsEmpty ^ other.IsEmpty)
        {
            return false;
        }

        return Ranges.SequenceEqual(other.Ranges);
    }

    public static bool operator ==(RingRangeCollection left, RingRangeCollection right) => left.Equals(right);

    public static bool operator !=(RingRangeCollection left, RingRangeCollection right) => !(left == right);

    public override bool Equals(object? obj) => obj is RingRangeCollection range && Equals(range);
    public override int GetHashCode()
    {
        var result = new HashCode();
        result.Add(Ranges.Length);
        if (!Ranges.IsDefaultOrEmpty)
        {
            foreach (var range in Ranges)
            {
                result.Add(range);
            }
        }

        return result.ToHashCode();
    }

    public ImmutableArray<RingRange>.Enumerator GetEnumerator() => Ranges.GetEnumerator();

    public override string ToString() => $"{this}";
    string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

    bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        => destination.TryWrite($"({Ranges.Length} subranges), {SizePercent:0.00}%", out charsWritten);
}