using System;
using System.Globalization;
using System.Text;

namespace Orleans.Runtime
{
    /// <summary>
    /// Abstract class for histogram value statistics, instantiate either HistogramValueStatistic or LinearHistogramValueStatistic
    /// </summary>
    internal abstract class HistogramValueStatistic
    {
        protected object    Lockable;
        protected CounterStatistic[] Buckets;
        
        public abstract void AddData(long data);
        public abstract void AddData(TimeSpan data);

        protected HistogramValueStatistic(string name, int numBuckets)
        {
            Lockable = new object();
            Buckets = new CounterStatistic[numBuckets];

            // Create a hidden counter per bucket to reuse the counter code for efficient counting without reporting each individual counter as a statistic.
            for (int i = 0; i < numBuckets; i++)
                Buckets[i] = CounterStatistic.FindOrCreate(
                    new StatisticName(String.Format("{0}.Bucket#{1}", name, i)),
                    false,
                    CounterStorage.DontStore,
                    isHidden: true);
        }

        internal string PrintHistogram()
        {
            return PrintHistogramImpl(false);
        }

        internal string PrintHistogramInMillis()
        {
            return PrintHistogramImpl(true);
        }

        protected void AddToCategory(uint histogramCategory)
        {
            histogramCategory = Math.Min(histogramCategory, (uint)(Buckets.Length - 1));
            Buckets[histogramCategory].Increment();
        }

        protected abstract double BucketStart(int i);

        protected abstract double BucketEnd(int i);

        protected bool IsLastBucket(int i)
        {
            return i == Buckets.Length - 1;
        }

        protected string PrintHistogramImpl(bool inMilliseconds)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Buckets.Length; i++)
            {
                long bucket = Buckets[i].GetCurrentValue();
                if (bucket > 0)
                {
                    double start = BucketStart(i);
                    double end = BucketEnd(i);
                    if (inMilliseconds)
                    {
                        string one =
                            IsLastBucket(i) ?
                                "EOT" :
                                TimeSpan.FromTicks((long) end).TotalMilliseconds.ToString();
                        sb.Append(String.Format(CultureInfo.InvariantCulture, "[{0}:{1}]={2}, ", TimeSpan.FromTicks((long)start).TotalMilliseconds, one, bucket));
                    }
                    else
                    {
                        sb.Append(String.Format(CultureInfo.InvariantCulture, "[{0}:{1}]={2}, ", start, end, bucket));
                    }
                }
            }
            return sb.ToString();
        }
    }

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

    /// <summary>
    /// Histogram created where buckets are uniform size
    /// </summary>
    internal class LinearHistogramValueStatistic : HistogramValueStatistic
    {
        private readonly double bucketWidth;

        private LinearHistogramValueStatistic(string name, int numBuckets, double maximumValue)
            : base(name, numBuckets)
        {
            bucketWidth = maximumValue / numBuckets;
        }

        public static LinearHistogramValueStatistic Create_LinearHistogram(StatisticName name, int numBuckets, double maximumValue)
        {
            var hist = new LinearHistogramValueStatistic(name.Name, numBuckets, maximumValue);
            StringValueStatistic.FindOrCreate(name, hist.PrintHistogram);
            return hist;
        }

        public static LinearHistogramValueStatistic Create_LinearHistogram_ForTiming(StatisticName name, int numBuckets, TimeSpan maximumValue)
        {
            var hist = new LinearHistogramValueStatistic(name.Name, numBuckets, maximumValue.Ticks);
            StringValueStatistic.FindOrCreate(name, hist.PrintHistogramInMillis);
            return hist;
        }

        public override void AddData(TimeSpan data)
        {
            uint histogramCategory = (uint)(data.Ticks / bucketWidth);
            AddToCategory(histogramCategory);
        }

        public override void AddData(long data)
        {
            uint histogramCategory = (uint)(data / bucketWidth);
            AddToCategory(histogramCategory);
        }

        protected override double BucketStart(int i)
        {
            return i * bucketWidth;
        }

        protected override double BucketEnd(int i)
        {
            if (IsLastBucket(i))
            {
                return Double.MaxValue;
            }
            return (i + 1) * bucketWidth;
        }
    }
}
 
