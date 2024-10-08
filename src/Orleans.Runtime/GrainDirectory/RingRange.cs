using System;
using System.Collections.Generic;
using System.Diagnostics;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

/// <summary>
/// Represents a contiguous range of zero or more values on a ring.
/// </summary>
[GenerateSerializer, Immutable, Alias(nameof(RingRange))]
internal readonly struct RingRange : IEquatable<RingRange>, ISpanFormattable, IComparable<uint>
{
    // The exclusive starting point for the range.
    //  Note that _start == _end == 1 is used as a special value to represent a full range.
    [Id(0)]
    private readonly uint _start;

    // The inclusive ending point for the range.
    //  Note that _start == _end == 1 is used as a special value to represent a full range.
    [Id(1)]
    private readonly uint _end;

    public bool IsEmpty => _start == _end && _start == 0;

    public bool IsFull => _start == _end && _start != 0;

    // Whether the range includes uint.MaxValue.
    internal bool IsWrapped => _start >= _end && _start != 0;

    public static RingRange Full { get; } = new (1, 1);

    public static RingRange Empty { get; } = new (0, 0);

    public uint Start => IsFull ? 0 : _start;

    public uint End => IsFull ? 0 : _end;

    private RingRange(uint start, uint end)
    {
        _start = start == end && start > 1 ? 1 : start;
        _end = start == end && start > 1 ? 1 : end;
    }

    // For internal use only.
    internal static RingRange Create(uint start, uint end) => new (start, end);

    /// <summary>
    /// Creates a range representing a single point.
    /// </summary>
    /// <param name="point">The point which the range will include.</param>
    /// <returns>A range including only <paramref name="point"/>.</returns>
    public static RingRange FromPoint(uint point) => new (unchecked(point - 1), point);

    /// <summary>
    /// Gets the size of the range.
    /// </summary>
    public uint Size
    {
        get
        {
            if (_start == _end)
            {
                // Empty
                if (_start == 0) return 0;

                // Full
                return uint.MaxValue;
            }

            // Normal
            if (_end > _start) return _end - _start;

            // Wrapped
            return uint.MaxValue - _start + _end;
        }
    }

    public int CompareTo(uint point)
    {
        if (Contains(point))
        {
            return 0;
        }

        var start = Start;
        if (IsWrapped)
        {
            // Start > End (wrap-around case)
            if (point <= start)
            {
                // Range starts after N (range > N)
                return -1;
            }

            // n > _end
            // Range starts & ends before N (range < N)
            return 1;
        }

        if (point <= start)
        {
            // Range starts after N (range > N)
            return 1;
        }

        // n > _end
        // Range starts & ends before N (range < N)
        return -1;
    }

    /// <summary>
    /// Checks if n is element of (Start, End], while remembering that the ranges are on a ring
    /// </summary>
    /// <returns>true if n is in (Start, End], false otherwise</returns>
    internal bool Contains(GrainId grainId) => Contains(grainId.GetUniformHashCode());

    /// <summary>
    /// checks if n is element of (Start, End], while remembering that the ranges are on a ring
    /// </summary>
    /// <param name="point"></param>
    /// <returns>true if n is in (Start, End], false otherwise</returns>
    public bool Contains(uint point)
    {
        if (IsEmpty)
        {
            return false;
        }

        var num = point;
        if (Start < End)
        {
            return num > Start && num <= End;
        }

        // Start > End
        return num > Start || num <= End;
    }

    public float SizePercent => Size * (100.0f / uint.MaxValue);

    public bool Equals(RingRange other) => _start == other._start && _end == other._end;

    public override bool Equals(object? obj) => obj is RingRange other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_start, _end);

    public override string ToString() => $"{this}";

    string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

    bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        return IsEmpty
            ? destination.TryWrite($"(0, 0) 0.00%", out charsWritten)
            : IsFull
                ? destination.TryWrite($"(0, 0] (100.00%)", out charsWritten)
                : destination.TryWrite($"(0x{Start:X8}, 0x{End:X8}] ({SizePercent:0.00}%)", out charsWritten);
    }

    public bool Intersects(RingRange other) => !IsEmpty && !other.IsEmpty && (Equals(other) || Contains(other.End) || other.Contains(End));

    internal RingRange Complement()
    {
        if (IsEmpty)
        {
            return Full;
        }

        if (IsFull)
        {
            return Empty;
        }

        return new RingRange(End, Start);
    }

    internal IEnumerable<RingRange> Intersections(RingRange other)
    {
        if (!Intersects(other))
        {
            // No intersections.
            yield break;
        }

        if (IsFull)
        {
            // One intersection, the other range.
            yield return other;
        }
        else if (other.IsFull)
        {
            yield return this;
        }
        else if (IsWrapped ^ other.IsWrapped)
        {
            var wrapped = IsWrapped ? this : other;
            var normal = IsWrapped ? other : this;
            var (normalStart, normalEnd) = (normal.Start, normal.End);
            var (wrappedStart, wrappedEnd) = (wrapped.Start, wrapped.End);

            // There are possibly two intersections, between the normal and wrapped range.
            //         low         high
            // ...---NB====WE----WB====NE----...

            // Intersection at the low side.
            if (wrappedEnd > normalStart)
            {
                // ---NB====WE---
                yield return new RingRange(normalStart, wrappedEnd);
            }

            // Intersection at the high side.
            if (wrappedStart < normalEnd)
            {
                // ---WB====NE---
                yield return new RingRange(wrappedStart, normalEnd);
            }
        }
        else
        {
            yield return new RingRange(Math.Max(Start, other.Start), Math.Min(End, other.End));
        }
    }

    // Gets the set difference: the sub-ranges which are in this range but are not in the 'other' range.
    internal IEnumerable<RingRange> Difference(RingRange other)
    {
        // Additions are the intersections between this range and the inverse of the previous range.
        foreach (var addition in Intersections(other.Complement()))
        {
            Debug.Assert(!addition.Intersects(other));
            Debug.Assert(addition.Intersects(this));
            yield return addition;
        }
    }

    public static bool operator ==(RingRange left, RingRange right) => left.Equals(right);

    public static bool operator !=(RingRange left, RingRange right) => !(left == right);
}
