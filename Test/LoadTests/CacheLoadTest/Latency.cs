// -----------------------------------------------------------------------
// <copyright file="Latency.cs" company="">
// TODO: Update copyright text.
// </copyright>
// -----------------------------------------------------------------------

namespace PresenceConsoleTest
{
    using System;

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
        private double totalValues;

        /// <summary>The standardDeviation value of a set of latencies.</summary>
        private double standardDeviation;

        /// <summary>The sum of all values in a set of latencies.</summary>
        private double sum;

        /// <summary>The sum of all values squared in a set of latencies.</summary>
        private double sum_Sqr;
        
        /// <summary>The variance value of a set of latencies.</summary>
        private double variance;

        /// <summary>Gets the total values in the set of latencies.</summary>
        public double TotalValues
        {
            get { return totalValues; }
        }

        /// <summary>Gets the maximum value of a set of latencies.</summary>
        public TimeSpan Maximum
        {
            get { return new TimeSpan(maximum); }
        }

        /// <summary>Gets the mean value of a set of latencies.</summary>
        public TimeSpan Mean
        {
            get { return new TimeSpan(Convert.ToInt64(mean)); }
        }

        /// <summary>Gets the minimum value of a set of latencies.</summary>
        public TimeSpan Minimum 
        { 
            get { return new TimeSpan(minimum); }
        }

        /// <summary>Gets the Variance value of a set of latencies.</summary>
        public TimeSpan StandardDeviation
        {
            get { return new TimeSpan(Convert.ToInt64(standardDeviation)); }
        }

        /// <summary>Gets the Variance value of a set of latencies.</summary>
        public TimeSpan Variance
        {
            get { return new TimeSpan(Convert.ToInt64(variance)); }
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

                maximum = Math.Max(maximum, latency);
                totalValues = totalValues + 1;
                sum = sum + latency;
                sum_Sqr = sum_Sqr + (latency * latency);
                mean = sum / totalValues;
                variance = (sum_Sqr - (sum * mean)) / totalValues;
                standardDeviation = Math.Sqrt(variance);
            }
        }

        /// <summary>Adds set of latency readings that are stored in a latency object.</summary>
        /// <param name="latency">The latency object to add.</param>
        public void AddLatencyReading(Latency latency)
        {
            lock (this)
            {
                minimum = Math.Min(minimum, latency.minimum);
                maximum = Math.Max(maximum, latency.maximum);
                totalValues = totalValues + latency.totalValues;
                sum = sum + latency.sum;
                sum_Sqr = sum_Sqr + latency.sum_Sqr;
                totalValues = totalValues + latency.totalValues;
                mean = sum / totalValues;
                variance = (sum_Sqr - (sum * mean)) / totalValues;
                standardDeviation = Math.Sqrt(variance);
            }
        }

        /// <summary>
        /// The ToString implementation for the latency class.
        /// </summary>
        /// <returns>A string representation of the latency class.</returns>
        public override string ToString()
        {
            return string.Format(
                "Min: {0} ms, Max: {1} ms, Mean: {2} ms, Std Dev: {3} ms", 
                Minimum.TotalMilliseconds.ToString(), 
                Maximum.TotalMilliseconds.ToString(), 
                Mean.TotalMilliseconds.ToString(), 
                StandardDeviation.TotalMilliseconds.ToString());
        }
    }
}
