using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.LogConsistency
{
    /// <summary>
    /// Interface for diagnostics.
    /// </summary>
    public interface ILogConsistencyDiagnostics
    {

        /// <summary>Gets a list of all currently unresolved connection issues.</summary>
        IEnumerable<ConnectionIssue> UnresolvedConnectionIssues { get; }

        /// <summary>Turns on the statistics collection for this log-consistent grain.</summary>
        void EnableStatsCollection();

        /// <summary>Turns off the statistics collection for this log-consistent grain.</summary>
        void DisableStatsCollection();

        /// <summary>Gets the collected statistics for this log-consistent grain.</summary>
        LogConsistencyStatistics GetStats();

    }

    /// <summary>
    /// A collection of statistics for grains using log-consistency. See <see cref="ILogConsistentGrain"/>
    /// </summary>
    public class LogConsistencyStatistics
    {
        /// <summary>
        /// A map from event names to event counts
        /// </summary>
        public Dictionary<String, long> EventCounters;
        /// <summary>
        /// A list of all measured stabilization latencies
        /// </summary>
        public List<int> StabilizationLatenciesInMsecs;
    }
}
