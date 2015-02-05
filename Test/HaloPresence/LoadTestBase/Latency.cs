// -----------------------------------------------------------------------
// <copyright file="Latency.cs" company="">
// TODO: Update copyright text.
// </copyright>
// -----------------------------------------------------------------------

namespace LoadTestBase
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// This class provide latency information such as Min, Max, Mean and Std Deviation.
    /// </summary>
    [Serializable]
    public class Latency
    {
        /// <summary>The maximum value of a set of latencies.</summary>
        private long maximum = 0;

        /// <summary>The mean value of a set of latencies.</summary>
        private double mean;

        /// <summary>
        /// The minimum value of a set of latencies.  
        /// Setting to max value initially so the first value will be the minimum instead of 0.
        /// </summary>
        private long minimum = long.MaxValue;

        /// <summary>The total number of latencies that have been submitted in a set of latencies.</summary>
        private long totalValues;

        /// <summary>The standardDeviation value of a set of latencies.</summary>
        private double standardDeviation;

        /// <summary>The sum of all values in a set of latencies.</summary>
        private double sum;

        /// <summary>The sum of all values squared in a set of latencies.</summary>
        private double sum_Sqr;
        
        /// <summary>The variance value of a set of latencies.</summary>
        private double variance;

        /// <summary>
        /// List of latency values.  Used to report percentile values like 50th anf 98th percentile
        /// </summary>
        public List<long> latencyValues = new List<long>(100000);

        /// <summary>
        /// Counts the number of request and take one out of every thousand.
        /// </summary>
        private int requestCount;

        /// <summary>
        /// The max size the latency list.
        /// </summary>
        private const int maxSizeOfLatencyList = 100000;

        /// <summary>Gets the total values in the set of latencies.</summary>
        public double TotalValues
        {
            get { return totalValues; }
        }

        /// <summary>Gets the maximum value of a set of latencies.</summary>
        public TimeSpan Maximum
        {
            get { return TimeSpan.FromTicks(maximum); }
        }

        /// <summary>Gets the mean value of a set of latencies.</summary>
        public TimeSpan Mean
        {
            get { return TimeSpan.FromTicks(Convert.ToInt64(mean)); }
        }

        /// <summary>Gets the minimum value of a set of latencies.</summary>
        public TimeSpan Minimum 
        {
            get { return TimeSpan.FromTicks(minimum); }
        }

        /// <summary>Gets the Variance value of a set of latencies.</summary>
        public TimeSpan StandardDeviation
        {
            get { return TimeSpan.FromTicks(Convert.ToInt64(standardDeviation)); }
        }

        /// <summary>Gets the Variance value of a set of latencies.</summary>
        public TimeSpan Variance
        {
            get { return TimeSpan.FromTicks(Convert.ToInt64(variance)); }
        }

        public TimeSpan Median
        {
            get { return TimeSpan.FromTicks(Convert.ToInt64(GetPercentile(latencyValues.ToArray(), 50))); }
        }

        public TimeSpan NinetiethPercent
        {
            get { return TimeSpan.FromTicks(Convert.ToInt64(GetPercentile(latencyValues.ToArray(), 90))); }
        }

        public TimeSpan NinetyFifthPercent
        {
            get { return TimeSpan.FromTicks(Convert.ToInt64(GetPercentile(latencyValues.ToArray(), 95))); }
        }

        public TimeSpan NinetyNinethPercent
        {
            get { return TimeSpan.FromTicks(Convert.ToInt64(GetPercentile(latencyValues.ToArray(), 99))); }
        }

        /// <summary>Adds a single latency reading that is the number of ticks of a TimeSpan.</summary>
        /// <param name="latency">The latency value to add</param>
        public void AddLatencyReading(long latency)
        {
            lock (this)
            {
                if (latency != 0) // Doing a check to prevent 0 latency which isn't possible
                {
                    minimum = Math.Min(minimum, latency);
                }

                AddLatencyValue(latency);
                maximum = Math.Max(maximum, latency);
                totalValues++;
                sum = sum + latency;
                sum_Sqr = sum_Sqr + (latency * latency);
                mean = sum / totalValues;
                variance = (sum_Sqr - (sum * mean)) / totalValues;
                standardDeviation = Math.Sqrt(variance);
            }
        }

        /// <summary>Adds set of latency readings that are stored in a latency object.</summary>
        /// <param name="latency">The latency object to add.</param>
        public void AddLatencyReadings(Latency latency)
        {
            lock (this)
            {
                lock (latency)
                {
                    AddLatencyValues(latency.latencyValues);
                }

                minimum = Math.Min(minimum, latency.minimum);
                maximum = Math.Max(maximum, latency.maximum);
                totalValues = totalValues + latency.totalValues;
                sum = sum + latency.sum;
                sum_Sqr = sum_Sqr + latency.sum_Sqr;
                mean = sum / totalValues;
                variance = (sum_Sqr - (sum * mean)) / totalValues;
                standardDeviation = Math.Sqrt(variance);
            }
        }

        private static long GetPercentile(long[] values, int percentile)
        {
            if (values.Length == 0)
            {
                return 0;
            }

            Array.Sort(values);
            int size = values.Length;
            int percentileIndex = (size * percentile)/100;
            if (percentileIndex >= size) 
                percentileIndex = size - 1;
            return values[percentileIndex];
        }

        private void AddLatencyValue(long latency)
        {
            ////if (latencyValues.Count == maxSizeOfLatencyList)
            ////{
            ////    latencyValues.RemoveAt(0);
            ////}
            requestCount++;

            //if (requestCount == 100)
            //{
            //    requestCount = 0;
                latencyValues.Add(latency);
            //}
        }

        private void AddLatencyValues(List<long> latencies)
        {
            latencyValues.AddRange(latencies);
        }

        /// <summary>
        /// The ToString implementation for the latency class.
        /// </summary>
        /// <returns>A string representation of the latency class.</returns>
        public override string ToString()
        {
            return string.Format(
                "Min: {0} ms, Max: {1} ms, Mean: {2} ms, Median: {3} ms, 90th: {4} ms, 95th: {5} ms, 99th: {6} ms, Std Dev: {7} ms", 
                Minimum.TotalMilliseconds.ToString(), 
                Maximum.TotalMilliseconds.ToString(), 
                Mean.TotalMilliseconds.ToString(),
                Median.TotalMilliseconds.ToString(),
                NinetiethPercent.TotalMilliseconds.ToString(),
                NinetyFifthPercent.TotalMilliseconds.ToString(),
                NinetyNinethPercent.TotalMilliseconds.ToString(),
                StandardDeviation.TotalMilliseconds.ToString());
        }
    }
}
