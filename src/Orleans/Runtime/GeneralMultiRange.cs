using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Runtime
{
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
}