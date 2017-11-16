using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using System;

namespace Orleans.Hosting
{
    /// <summary>
    /// The StatisticsOptions type contains various statistics output related options.
    /// </summary>
    public class StatisticsOptions
    {
        /// <summary>
        /// The MetricsTableWriteInterval property specifies the frequency of updating the metrics in Azure table.
        ///  The default is 30 seconds.
        /// </summary>
        public TimeSpan MetricsTableWriteInterval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The PerfCounterWriteInterval property specifies the frequency of updating the windows performance counters.
        /// The default is 30 seconds.
        /// </summary>
        public TimeSpan PerfCountersWriteInterval { get; set; } = Constants.INFINITE_TIMESPAN;

        /// <summary>
        /// The LogWriteInterval property specifies the frequency of updating the statistics in the log file.
        /// The default is 5 minutes.
        /// </summary>
        public TimeSpan LogWriteInterval { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// The WriteLogStatisticsToTable property specifies whether log statistics should also be written into a separate, special Azure table.
        ///  The default is yes.
        /// </summary>
        public bool WriteLogStatisticsToTable { get; set; } = true;

        /// <summary>
        /// The CollectionLevel property specifies the verbosity level of statistics to collect. The default is Info.
        /// </summary>
        public StatisticsLevel CollectionLevel { get; set; } = StatisticsLevel.Info;

        /// <summary>
        /// The ProviderName property specifies the name of the configured statistics provider.
        /// </summary>
        public string ProviderName { get; set; }
    }
}
