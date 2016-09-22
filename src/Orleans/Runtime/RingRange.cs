using System;
using System.Collections.Generic;
using System.Linq;


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

    [Serializable]
    internal class SingleRange : IRingRangeInternal, IEquatable<SingleRange>
    {
        private readonly uint begin;
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
   
        public double RangePercentage()
        {
            return ((double)RangeSize() / (double)RangeFactory.RING_SIZE) * ((double)100.0);
        }

        #region IEquatable<SingleRange> Members

        public bool Equals(SingleRange other)
        {
            return other != null && begin == other.begin && end == other.end;
        }

        #endregion

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
                return String.Format("<(0 0], Size=x{0,8:X8}, %Ring={1:0.000}%>", RangeSize(), RangePercentage());
            }
            return String.Format("<(x{0,8:X8} x{1,8:X8}], Size=x{2,8:X8}, %Ring={3:0.000}%>", begin, end, RangeSize(), RangePercentage());
        }

        public string ToCompactString()
        {
            return ToString();
        }

        public string ToFullString()
        {
            return ToString();
        }
    }

    internal static class RangeFactory
    {
        public const long RING_SIZE = ((long)uint.MaxValue) + 1;

        public static IRingRange CreateFullRange()
        {
            return new SingleRange(0, 0);
        }

        public static IRingRange CreateRange(uint begin, uint end)
        {
            return new SingleRange(begin, end);
        }

        public static IRingRange CreateRange(List<IRingRange> inRanges)
        {
            return new GeneralMultiRange(inRanges);
        }

        public static EquallyDividedMultiRange CreateEquallyDividedMultiRange(IRingRange range, int numSubRanges)
        {
            return new EquallyDividedMultiRange(range, numSubRanges);
        }

        public static IEnumerable<SingleRange> GetSubRanges(IRingRange range)
        {
            if (range is SingleRange)
            {
                return new SingleRange[] { (SingleRange)range };
            }
            else if (range is GeneralMultiRange)
            {
                return ((GeneralMultiRange)range).Ranges;
            }
            return null;
        }
    }

    [Serializable]
    internal class GeneralMultiRange : IRingRangeInternal
    {
        private readonly List<SingleRange> ranges;
        private readonly long rangeSize;
        private readonly double rangePercentage;

        internal IEnumerable<SingleRange> Ranges { get { return ranges; } }

        internal GeneralMultiRange(IEnumerable<IRingRange> inRanges)
        {
            ranges = inRanges.Cast<SingleRange>().ToList();
            if (ranges.Count == 0)
            {
                rangeSize = 0;
                rangePercentage = 0;
            }
            else
            {
                rangeSize = ranges.Sum(r => r.RangeSize());
                rangePercentage = ranges.Sum(r => r.RangePercentage());
            }
        }

        public bool InRange(uint n)
        {
            foreach (IRingRange s in Ranges)
            {
                if (s.InRange(n)) return true;
            }
            return false;
        }
        public bool InRange(GrainReference grainReference)
        {
            return InRange(grainReference.GetUniformHashCode());
        }

        public long RangeSize()
        {
            return rangeSize;
        }

        public double RangePercentage()
        {
            return rangePercentage;
        }

        public override string ToString()
        {
            return ToCompactString();
        }

        public string ToCompactString()
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

    [Serializable]
    internal class EquallyDividedMultiRange
    {
        [Serializable]
        private class EquallyDividedSingleRange
        {
            private readonly List<SingleRange> ranges;

            internal EquallyDividedSingleRange(SingleRange singleRange, int numSubRanges)
            {
                ranges = new List<SingleRange>();
                if (numSubRanges == 0) throw new ArgumentException("numSubRanges is 0.", "numSubRanges");
                
                if (numSubRanges == 1)
                {
                    ranges.Add(singleRange);
                }
                else
                {    
                    uint uNumSubRanges = checked((uint)numSubRanges);
                    uint portion = (uint)(singleRange.RangeSize() / uNumSubRanges);
                    uint remainder = (uint)(singleRange.RangeSize() - portion * uNumSubRanges);
                    uint start = singleRange.Begin;
                    for (uint i = 0; i < uNumSubRanges; i++)
                    {
                        // (Begin, End]
                        uint end = (unchecked(start + portion));
                            // I want it to overflow on purpose. It will do the right thing.
                        if (remainder > 0)
                        {
                            end++;
                            remainder--;
                        }
                        ranges.Add(new SingleRange(start, end));
                        start = end; // nextStart
                    }
                }
            }

            internal SingleRange GetSubRange(int mySubRangeIndex)
            {
                return ranges[mySubRangeIndex];
            }
        }

        private readonly Dictionary<int, IRingRangeInternal> multiRanges;
        private readonly long rangeSize;
        private readonly double rangePercentage;

        // This class takes a range and devides it into X (x being numSubRanges) equal ranges.
        public EquallyDividedMultiRange(IRingRange range, int numSubRanges)
        {
            multiRanges = new Dictionary<int, IRingRangeInternal>();
            if (range is SingleRange)
            {
                var fullSingleRange = range as SingleRange;
                var singleDevided = new EquallyDividedSingleRange(fullSingleRange, numSubRanges);
                for (int i = 0; i < numSubRanges; i++)
                {
                    var singleRange = singleDevided.GetSubRange(i);
                    multiRanges[i] = singleRange;
                }
            }
            else if (range is GeneralMultiRange)
            {
                var fullMultiRange = range as GeneralMultiRange;
                // Take each of the single ranges in the multi range and divide each into equal sub ranges.
                // Then go over all those and group them by sub range index.
                var allSinglesDevided = new List<EquallyDividedSingleRange>();
                foreach (var singleRange in fullMultiRange.Ranges)
                {
                    var singleDevided = new EquallyDividedSingleRange(singleRange, numSubRanges);
                    allSinglesDevided.Add(singleDevided);
                }

                for (int i = 0; i < numSubRanges; i++)
                {
                    var singlesForThisIndex = new List<IRingRange>();
                    foreach (var singleDevided in allSinglesDevided)
                    {
                        IRingRange singleRange = singleDevided.GetSubRange(i);
                        singlesForThisIndex.Add(singleRange);
                    }
                    IRingRangeInternal multi = (IRingRangeInternal)RangeFactory.CreateRange(singlesForThisIndex);
                    multiRanges[i] = multi;
                }
            }
            if (multiRanges.Count == 0)
            {
                rangeSize = 0;
                rangePercentage = 0;
            }
            else
            {
                rangeSize = multiRanges.Values.Sum(r => r.RangeSize());
                rangePercentage = multiRanges.Values.Sum(r => r.RangePercentage());
            }
        }

        internal IRingRange GetSubRange(int mySubRangeIndex)
        {
            return multiRanges[mySubRangeIndex];
        }

        public override string ToString()
        {
            return ToCompactString();
        }

        public string ToCompactString()
        {
            if (multiRanges.Count == 0) return "Empty EquallyDevidedMultiRange";
            if (multiRanges.Count == 1) return multiRanges.First().Value.ToString();

            return String.Format("<EquallyDevidedMultiRange: Size=x{0,8:X8}, %Ring={1:0.000}%>", rangeSize, rangePercentage);
        }

        public string ToFullString()
        {
            if (multiRanges.Count == 0) return "Empty EquallyDevidedMultiRange";
            if (multiRanges.Count == 1) return multiRanges.First().Value.ToFullString();
            return String.Format("<EquallyDevidedMultiRange: Size=x{0,8:X8}, %Ring={1:0.000}%, {2} Ranges: {3}>", rangeSize, rangePercentage, multiRanges.Count,
                Utils.DictionaryToString(multiRanges, r => r.ToFullString()));
        }
    }
}
