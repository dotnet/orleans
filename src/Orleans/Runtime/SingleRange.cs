using System;


namespace Orleans.Runtime
{
    // This is the public interface to be used by the consistent ring

    // This is the internal interface to be used only by the different range implementations.

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
}
