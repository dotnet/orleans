using System.Collections.Generic;

namespace Orleans.Runtime
{
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
}