using System;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    // This is the public interface to be used by the consistent ring
    public interface IRingRange
    {
        /// <summary>
        /// Check if <paramref name="n"/> is our responsibility to serve
        /// </summary>
        /// <returns>true if the reminder is in our responsibility range, false otherwise</returns>
        bool InRange(uint n);

        bool InRange(GrainReference grainReference);
    }

    // This is the internal interface to be used only by the different range implementations.
    internal interface IRingRangeInternal : IRingRange
    {
        long RangeSize();
        double RangePercentage();
        string ToFullString();
    }

    public interface ISingleRange : IRingRange
    {
        /// <summary>
        /// Exclusive
        /// </summary>
        uint Begin { get; }
        /// <summary>
        /// Inclusive
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
            if (begin == 0 && end == 0)
            {
                return "<(0 0], Size=x100000000, %Ring=100%>";
            }
            return String.Format("<(x{0,8:X8} x{1,8:X8}], Size=x{2,8:X8}, %Ring={3:0.000}%>", begin, end, RangeSize(), RangePercentage());
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

    public static class RangeFactory
    {
        public const long RING_SIZE = ((long)uint.MaxValue) + 1;
        private static readonly GeneralMultiRange EmptyRange = new(new());
        internal static readonly SingleRange FullRange = new(0, 0);

        public static IRingRange CreateFullRange() => FullRange;

        public static IRingRange CreateRange(uint begin, uint end) => new SingleRange(begin, end);

        public static IRingRange CreateRange(List<IRingRange> inRanges) => inRanges.Count switch
        {
            0 => EmptyRange,
            1 => inRanges[0],
            _ => GeneralMultiRange.Create(inRanges)
        };

        internal static IRingRange GetEquallyDividedSubRange(IRingRange range, int numSubRanges, int mySubRangeIndex)
            => EquallyDividedMultiRange.GetEquallyDividedSubRange(range, numSubRanges, mySubRangeIndex);

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
        [Id(3)]

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
            return String.Format("<MultiRange: Size=x{0,8:X8}, %Ring={1:0.000}%>", RangeSize(), RangePercentage());
        }

        public string ToFullString()
        {
            if (ranges.Count == 0) return "Empty MultiRange";
            if (ranges.Count == 1) return ranges[0].ToString();
            return String.Format("<MultiRange: Size=x{0,8:X8}, %Ring={1:0.000}%, {2} Ranges: {3}>", RangeSize(), RangePercentage(), ranges.Count, Utils.EnumerableToString(ranges, r => r.ToFullString()));
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

