using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Orleans.Hosting
{
    /// <summary>
    /// Statistics output related options for silo.
    /// </summary>
    public class SiloStatisticsOptions : StatisticsOptions
    {
        public static readonly TimeSpan SILO_DEFAULT_PERF_COUNTERS_WRITE_PERIOD = TimeSpan.FromSeconds(30);

        public SiloStatisticsOptions()
        {
            this.PerfCountersWriteInterval = SILO_DEFAULT_PERF_COUNTERS_WRITE_PERIOD;
        }

        /// <summary>
        /// Interval in which deployment statistics are published.
        /// </summary>
        public TimeSpan DeploymentLoadPublisherRefreshTime { get; set; } = DEFAULT_DEPLOYMENT_LOAD_PUBLISHER_REFRESH_TIME;
        public static readonly TimeSpan DEFAULT_DEPLOYMENT_LOAD_PUBLISHER_REFRESH_TIME = TimeSpan.FromSeconds(1);
    }

    public class SiloStatisticsOptionsFormatter : StatisticsOptionsFormatter, IOptionFormatter<SiloStatisticsOptions>
    {
        public string Category { get; }

        public string Name => nameof(SiloStatisticsOptions);

        private SiloStatisticsOptions options;

        public SiloStatisticsOptionsFormatter(IOptions<SiloStatisticsOptions> options)
            :base(options.Value)
        {
            this.options = options.Value;
        }

        public IEnumerable<string> Format()
        {
            List<string> format = base.FormatSharedOptions();
            format.AddRange(new List<string>
            {
                OptionFormattingUtilities.Format(nameof(this.options.DeploymentLoadPublisherRefreshTime), this.options.DeploymentLoadPublisherRefreshTime)
            });
            return format;
        }
    }
}
