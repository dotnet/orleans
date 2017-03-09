using System;
using System.Globalization;
using System.Text;

namespace Orleans.Runtime
{
    /// <summary>
    /// Abstract class for histgram value statistics, instantiate either HistogramValueStatistic or LinearHistogramValueStatistic
    /// </summary>
    internal abstract class HistogramValueStatistic
    {
        protected Object    Lockable;
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
}
 
