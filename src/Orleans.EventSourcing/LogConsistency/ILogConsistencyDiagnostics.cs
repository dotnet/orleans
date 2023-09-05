using System;
using System.Collections.Generic;

namespace Orleans.EventSourcing
{
    /// <summary>
    /// Interface for diagnostics.
    /// </summary>
    public interface ILogConsistencyDiagnostics
    {
        /// <summary>Turns on the statistics collection for this log-consistent grain.</summary>
        void EnableStatsCollection();

        /// <summary>Turns off the statistics collection for this log-consistent grain.</summary>
        void DisableStatsCollection();

        /// <summary>Gets the collected statistics for this log-consistent grain.</summary>
        LogConsistencyStatistics GetStats();

    }

    /// <summary>
    /// A collection of statistics for grains using log-consistency. See <see cref="LogConsistentGrain{T}"/>
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
