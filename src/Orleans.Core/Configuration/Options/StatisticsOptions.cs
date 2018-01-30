using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using System;
using System.Collections.Generic;

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
    }

    public class StatisticOptionsFormatter : IOptionFormatter<StatisticsOptions>
    {
        public string Category { get; }

        public string Name => nameof(StatisticsOptions);
        private StatisticsOptions options;
        public StatisticOptionsFormatter(IOptions<StatisticsOptions> options)
        {
            this.options = options.Value;
        }
        public IEnumerable<string> Format()
        {
            return new List<string>()
            {
                OptionFormattingUtilities.Format(nameof(options.MetricsTableWriteInterval), options.MetricsTableWriteInterval),
                OptionFormattingUtilities.Format(nameof(options.PerfCountersWriteInterval), options.PerfCountersWriteInterval),
                OptionFormattingUtilities.Format(nameof(options.LogWriteInterval), options.LogWriteInterval),
                OptionFormattingUtilities.Format(nameof(options.WriteLogStatisticsToTable), options.WriteLogStatisticsToTable),
                OptionFormattingUtilities.Format(nameof(options.CollectionLevel), options.CollectionLevel),
            };
        }
    }
}
