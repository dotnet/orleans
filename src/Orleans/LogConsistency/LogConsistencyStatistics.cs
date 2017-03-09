using System.Collections.Generic;

namespace Orleans.LogConsistency
{
    /// <summary>
    /// A collection of statistics for grains using log-consistency. See <see cref="ILogConsistentGrain"/>
    /// </summary>
    public class LogConsistencyStatistics
    {
        /// <summary>
        /// A map from event names to event counts
        /// </summary>
        public Dictionary<string, long> EventCounters;
        /// <summary>
        /// A list of all measured stabilization latencies
        /// </summary>
        public List<int> StabilizationLatenciesInMsecs;
    }
}