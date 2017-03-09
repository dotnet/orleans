using System;

namespace Orleans.Runtime
{
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