using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Histogram created where buckets grow exponentially
    /// </summary>
    internal class ExponentialHistogramValueStatistic : HistogramValueStatistic
    {
        private ExponentialHistogramValueStatistic(string name, int numBuckets)
            : base(name, numBuckets) 
        {
        }

        public static ExponentialHistogramValueStatistic Create_ExponentialHistogram(StatisticName name, int numBuckets)
        {
            var hist = new ExponentialHistogramValueStatistic(name.Name, numBuckets);
            StringValueStatistic.FindOrCreate(name, hist.PrintHistogram);
            return hist;
        }

        public static ExponentialHistogramValueStatistic Create_ExponentialHistogram_ForTiming(StatisticName name, int numBuckets)
        {
            var hist = new ExponentialHistogramValueStatistic(name.Name, numBuckets);
            StringValueStatistic.FindOrCreate(name, hist.PrintHistogramInMillis);
            return hist;
        }

        public override void AddData(TimeSpan data)
        {
            uint histogramCategory = (uint)Log2((ulong)data.Ticks);
            AddToCategory(histogramCategory);
        }

        public override void AddData(long data)
        {
            uint histogramCategory = (uint)Log2((ulong)data);
            AddToCategory(histogramCategory);
        }

        protected override double BucketStart(int i)
        {
            if (i == 0)
            {
                return 0.0;
            }
            return Math.Pow(2, i);
        }

        protected override double BucketEnd(int i)
        {
            if (IsLastBucket(i))
            {
                return Double.MaxValue;
            }
            return Math.Pow(2, i + 1) - 1;
        }

        // The log base 2 of an integer is the same as the position of the highest bit set (or most significant bit set, MSB). The following log base 2 methods are faster than this one. 
        // More impl. methods here: http://graphics.stanford.edu/~seander/bithacks.html
        private static uint Log2(ulong number)
        {
            uint r = 0; // r will be log2(number)

            while ((number >>= 1) != 0) // unroll for more speed...
            {
                r++;
            }
            return r;
        }
    }
}