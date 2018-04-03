using System;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Configuration
{
    /// <summary>
    /// The StatisticsOptions type contains various statistics output related options.
    /// </summary>
    public abstract class StatisticsOptions
    {
        /// <summary>
        /// The PerfCounterWriteInterval property specifies the frequency of updating the windows performance counters.
        /// The default is 30 seconds.
        /// </summary>
        public TimeSpan PerfCountersWriteInterval { get; set; } = DEFAULT_PERF_COUNTERS_WRITE_PERIOD;
        public static readonly TimeSpan DEFAULT_PERF_COUNTERS_WRITE_PERIOD = Constants.INFINITE_TIMESPAN;

        /// <summary>
        /// The LogWriteInterval property specifies the frequency of updating the statistics in the log file.
        /// The default is 5 minutes.
        /// </summary>
        public TimeSpan LogWriteInterval { get; set; } = DEFAULT_LOG_WRITE_PERIOD;
        public static readonly TimeSpan DEFAULT_LOG_WRITE_PERIOD = TimeSpan.FromMinutes(5);
        

        /// <summary>
        /// The CollectionLevel property specifies the verbosity level of statistics to collect. The default is Info.
        /// </summary>
        public StatisticsLevel CollectionLevel { get; set; } = DEFAULT_COLLECTION_LEVEL;
        public static readonly StatisticsLevel DEFAULT_COLLECTION_LEVEL = StatisticsLevel.Info;
    }
}
