using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Runtime
{
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