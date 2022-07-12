using System;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents a range or set of ranges around a virtual ring where points along the ring are identified using <see cref="uint"/> values.
    /// </summary>
    public interface IRingRange
    {
        /// <summary>
        /// Returns a value indicating whether <paramref name="value"/> is within this ring range.
        /// </summary>
        /// <param name="value">
        /// The value to check.
        /// </param>
        /// <returns><see langword="true"/> if the reminder is in our responsibility range, <see langword="false"/> otherwise.</returns>
        bool InRange(uint value);

        /// <summary>
        /// Returns a value indicating whether <paramref name="grainReference"/> is within this ring range.
        /// </summary>
        /// <param name="grainReference">
        /// The value to check.
        /// </param>
        /// <returns><see langword="true"/> if the reminder is in our responsibility range, <see langword="false"/> otherwise.</returns>
        bool InRange(GrainReference grainReference);
    }

    // This is the internal interface to be used only by the different range implementations.
    internal interface IRingRangeInternal : IRingRange
    {
        long RangeSize();
        double RangePercentage();
        string ToFullString();
    }

    /// <summary>
    /// Represents a single, contiguous range round a virtual ring where points along the ring are identified using <see cref="uint"/> values.
    /// </summary>
    /// <seealso cref="Orleans.Runtime.IRingRange" />
    public interface ISingleRange : IRingRange
    {
        /// <summary>
        /// Gets the exclusive lower bound of the range.
        /// </summary>
        uint Begin { get; }
        
        /// <summary>
        /// Gets the inclusive upper bound of the range.
        /// </summary>
        uint End { get; }
    }

    [Serializable]
    [GenerateSerializer]
    internal sealed class SingleRange : IRingRangeInternal, IEquatable<SingleRange>, ISingleRange
    {
        [Id(1)]
        private readonly uint begin;
        [Id(2)]
        private readonly uint end;

        /// <summary>
        /// Exclusive
        /// </summary>
        public uint Begin { get { return begin; } }

        /// <summary>
        /// Inclusive
        /// </summary>
        public uint End { get { return end; } }

        public SingleRange(uint begin, uint end)
        {
            this.begin = begin;
            this.end = end;
        }

        public bool InRange(GrainReference grainReference)
        {
            return InRange(grainReference.GetUniformHashCode());
        }

        /// <summary>
        /// checks if n is element of (Begin, End], while remembering that the ranges are on a ring
        /// </summary>
        /// <param name="n"></param>
        /// <returns>true if n is in (Begin, End], false otherwise</returns>
        public bool InRange(uint n)
        {
            uint num = n;
            if (begin < end)
            {
                return num > begin && num <= end;
            }
            // Begin > End
            return num > begin || num <= end;
        }

        public long RangeSize()
        {
            if (begin < end)
            {
                return end - begin;
            }
            return RangeFactory.RING_SIZE - (begin - end);
        }

        public double RangePercentage() => RangeSize() * (100.0 / RangeFactory.RING_SIZE);

        public bool Equals(SingleRange other)
        {
            return other != null && begin == other.begin && end == other.end;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as SingleRange);
        }

        public override int GetHashCode()
        {
            return (int)(begin ^ end);
        }

        public override string ToString()
        {
            return begin == 0 && end == 0
                ? "<(0 0], Size=x100000000, %Ring=100%>"
                : $"<(x{begin:X8} x{end:X8}], Size=x{RangeSize():X8}, %Ring={RangePercentage():0.000}%>";
        }

        public string ToFullString()
        {
            return ToString();
        }

        internal bool Overlaps(SingleRange other) => Equals(other) || InRange(other.begin) || other.InRange(begin);

        internal SingleRange Merge(SingleRange other)
        {
            if ((begin | end) == 0 || (other.begin | other.end) == 0)
            {
                return RangeFactory.FullRange;
            }

            if (Equals(other))
            {
                return this;
            }

            if (InRange(other.begin))
            {
                return MergeEnds(other);
            }

            if (other.InRange(begin))
            {
                return other.MergeEnds(this);
            }

            throw new InvalidOperationException("Ranges don't overlap");
        }

        // other range begins inside this range, merge it based on where it ends
        private SingleRange MergeEnds(SingleRange other)
        {
            if (begin == other.end)
            {
                return RangeFactory.FullRange;
            }

            if (!InRange(other.end))
            {
                return new SingleRange(begin, other.end);
            }

            if (other.InRange(begin))
            {
                return RangeFactory.FullRange;
            }

            return this;
        }
    }

    /// <summary>
    /// Utility class for creating <see cref="IRingRange" /> values.
    /// </summary>
    public static class RangeFactory
    {
        /// <summary>
        /// The ring size.
        /// </summary>
        public const long RING_SIZE = ((long)uint.MaxValue) + 1;

        /// <summary>
        /// Represents an empty range.
        /// </summary>
        private static readonly GeneralMultiRange EmptyRange = new(new());
        /// <summary>
        /// Represents a full range.
        /// </summary>
        internal static readonly SingleRange FullRange = new(0, 0);

        /// <summary>
        /// Creates the full range.
        /// </summary>
        /// <returns>IRingRange.</returns>
        public static IRingRange CreateFullRange() => FullRange;

        /// <summary>
        /// Creates a new <see cref="IRingRange"/> representing the values between the exclusive lower bound, <paramref name="begin"/>, and the inclusive upper bound, <paramref name="end"/>.
        /// </summary>
        /// <param name="begin">The exclusive lower bound.</param>
        /// <param name="end">The inclusive upper bound.</param>
        /// <returns>A new <see cref="IRingRange"/> representing the values between the exclusive lower bound, <paramref name="begin"/>, and the inclusive upper bound, <paramref name="end"/>.</returns>
        public static IRingRange CreateRange(uint begin, uint end) => new SingleRange(begin, end);

        /// <summary>
        /// Creates a new <see cref="IRingRange"/> representing the union of all provided ranges.
        /// </summary>
        /// <param name="inRanges">The ranges.</param>
        /// <returns>A new <see cref="IRingRange"/> representing the union of all provided ranges.</returns>
        public static IRingRange CreateRange(List<IRingRange> inRanges) => inRanges.Count switch
        {
            0 => EmptyRange,
            1 => inRanges[0],
            _ => GeneralMultiRange.Create(inRanges)
        };

        /// <summary>
        /// Creates equally divided sub-ranges from the provided range and returns one sub-range from that range.
        /// </summary>
        /// <param name="range">The range.</param>
        /// <param name="numSubRanges">The number of sub-ranges.</param>
        /// <param name="mySubRangeIndex">The index of the sub-range to return.</param>
        /// <returns>The identified sub-range.</returns>
        internal static IRingRange GetEquallyDividedSubRange(IRingRange range, int numSubRanges, int mySubRangeIndex)
            => EquallyDividedMultiRange.GetEquallyDividedSubRange(range, numSubRanges, mySubRangeIndex);

        /// <summary>
        /// Gets the contiguous sub-ranges represented by the provided range.
        /// </summary>
        /// <param name="range">The range.</param>
        /// <returns>The contiguous sub-ranges represented by the provided range.</returns>
        public static IEnumerable<ISingleRange> GetSubRanges(IRingRange range) => range switch
        {
            ISingleRange single => new[] { single },
            GeneralMultiRange m => m.Ranges,
            _ => throw new NotSupportedException(),
        };
    }

    [Serializable]
    [GenerateSerializer]
    internal sealed class GeneralMultiRange : IRingRangeInternal
    {
        [Id(1)]
        private readonly List<SingleRange> ranges;
        [Id(2)]
        private readonly long rangeSize;

        internal List<SingleRange> Ranges => ranges;

        internal GeneralMultiRange(List<SingleRange> ranges)
        {
            this.ranges = ranges;
            foreach (var r in ranges)
                rangeSize += r.RangeSize();
        }

        internal static IRingRange Create(List<IRingRange> inRanges)
        {
            var ranges = inRanges.ConvertAll(r => (SingleRange)r);
            return HasOverlaps() ? Compact() : new GeneralMultiRange(ranges);

            bool HasOverlaps()
            {
                var last = ranges[0];
                for (var i = 1; i < ranges.Count; i++)
                {
                    if (last.Overlaps(last = ranges[i])) return true;
                }

                return false;
            }

            IRingRange Compact()
            {
                var lastIdx = 0;
                var last = ranges[0];
                for (var i = 1; i < ranges.Count; i++)
                {
                    var r = ranges[i];
                    if (last.Overlaps(r)) ranges[lastIdx] = last = last.Merge(r);
                    else ranges[++lastIdx] = last = r;
                }
                if (lastIdx == 0) return last;
                ranges.RemoveRange(++lastIdx, ranges.Count - lastIdx);
                return new GeneralMultiRange(ranges);
            }
        }

        public bool InRange(uint n)
        {
            foreach (var s in ranges)
            {
                if (s.InRange(n)) return true;
            }
            return false;
        }

        public bool InRange(GrainReference grainReference)
        {
            return InRange(grainReference.GetUniformHashCode());
        }

        public long RangeSize() => rangeSize;

        public double RangePercentage() => RangeSize() * (100.0 / RangeFactory.RING_SIZE);

        public override string ToString()
        {
            if (ranges.Count == 0) return "Empty MultiRange";
            if (ranges.Count == 1) return ranges[0].ToString();
            return $"<MultiRange: Size=x{RangeSize():X8}, %Ring={RangePercentage():0.000}%>";
        }

        public string ToFullString()
        {
            if (ranges.Count == 0) return "Empty MultiRange";
            if (ranges.Count == 1) return ranges[0].ToString();
            return $"<MultiRange: Size=x{RangeSize():X8}, %Ring={RangePercentage():0.000}%, {ranges.Count} Ranges: {Utils.EnumerableToString(ranges)}>";
        }
    }

    internal static class EquallyDividedMultiRange
    {
        private static SingleRange GetEquallyDividedSubRange(SingleRange singleRange, int numSubRanges, int mySubRangeIndex)
        {
            var rangeSize = singleRange.RangeSize();
            uint portion = (uint)(rangeSize / numSubRanges);
            uint remainder = (uint)(rangeSize - portion * numSubRanges);
            uint start = singleRange.Begin;
            for (int i = 0; i < numSubRanges; i++)
            {
                // (Begin, End]
                uint end = (unchecked(start + portion));
                // I want it to overflow on purpose. It will do the right thing.
                if (remainder > 0)
                {
                    end++;
                    remainder--;
                }
                if (i == mySubRangeIndex)
                    return new SingleRange(start, end);
                start = end; // nextStart
            }
            throw new ArgumentException(nameof(mySubRangeIndex));
        }

        // Takes a range and devides it into numSubRanges equal ranges and returns the subrange at mySubRangeIndex.
        public static IRingRange GetEquallyDividedSubRange(IRingRange range, int numSubRanges, int mySubRangeIndex)
        {
            if (numSubRanges <= 0) throw new ArgumentException(nameof(numSubRanges));
            if ((uint)mySubRangeIndex >= (uint)numSubRanges) throw new ArgumentException(nameof(mySubRangeIndex));

            if (numSubRanges == 1) return range;

            switch (range)
            {
                case SingleRange singleRange:
                    return GetEquallyDividedSubRange(singleRange, numSubRanges, mySubRangeIndex);

                case GeneralMultiRange multiRange:
                    switch (multiRange.Ranges.Count)
                    {
                        case 0: return multiRange;
                        case 1: return GetEquallyDividedSubRange(multiRange.Ranges[0], numSubRanges, mySubRangeIndex);
                        default:
                            // Take each of the single ranges in the multi range and divide each into equal sub ranges.
                            var singlesForThisIndex = new List<SingleRange>(multiRange.Ranges.Count);
                            foreach (var singleRange in multiRange.Ranges)
                                singlesForThisIndex.Add(GetEquallyDividedSubRange(singleRange, numSubRanges, mySubRangeIndex));
                            return new GeneralMultiRange(singlesForThisIndex);
                    }

                default: throw new ArgumentException(nameof(range));
            }
        }
    }
}

