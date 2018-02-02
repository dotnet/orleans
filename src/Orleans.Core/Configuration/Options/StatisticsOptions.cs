using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Hosting
{
    /// <summary>
    /// The StatisticsOptions type contains various statistics output related options.
    /// </summary>
    public abstract class StatisticsOptions
    {
        /// <summary>
        /// The MetricsTableWriteInterval property specifies the frequency of updating the metrics in Azure table.
        ///  The default is 30 seconds.
        /// </summary>
        public TimeSpan MetricsTableWriteInterval { get; set; } = DEFAULT_METRICS_TABLE_WRITE_PERIOD;
        public static readonly TimeSpan DEFAULT_METRICS_TABLE_WRITE_PERIOD = TimeSpan.FromSeconds(30);

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
        /// The WriteLogStatisticsToTable property specifies whether log statistics should also be written into a separate, special Azure table.
        ///  The default is yes.
        /// </summary>
        public bool WriteLogStatisticsToTable { get; set; } = DEFAULT_LOG_TO_TABLE;
        public const bool DEFAULT_LOG_TO_TABLE = true;

        /// <summary>
        /// The CollectionLevel property specifies the verbosity level of statistics to collect. The default is Info.
        /// </summary>
        public StatisticsLevel CollectionLevel { get; set; } = DEFAULT_COLLECTION_LEVEL;
        public static readonly StatisticsLevel DEFAULT_COLLECTION_LEVEL = StatisticsLevel.Info;
    }

    public abstract class StatisticsOptionsFormatter
    {
        private StatisticsOptions options;

        protected StatisticsOptionsFormatter(StatisticsOptions options)
        {
            this.options = options;
        }

        protected List<string> FormatSharedOptions()
        {
            return new List<string>()
            {
                OptionFormattingUtilities.Format(nameof(this.options.MetricsTableWriteInterval), this.options.MetricsTableWriteInterval),
                OptionFormattingUtilities.Format(nameof(this.options.PerfCountersWriteInterval), this.options.PerfCountersWriteInterval),
                OptionFormattingUtilities.Format(nameof(this.options.LogWriteInterval), this.options.LogWriteInterval),
                OptionFormattingUtilities.Format(nameof(this.options.WriteLogStatisticsToTable), this.options.WriteLogStatisticsToTable),
                OptionFormattingUtilities.Format(nameof(this.options.CollectionLevel), this.options.CollectionLevel),
            };
        }
    }
}
