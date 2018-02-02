using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Orleans.Hosting
{
    public class SiloStatisticsOptions : StatisticsOptions
    {
        public static readonly TimeSpan SILO_DEFAULT_PERF_COUNTERS_WRITE_PERIOD = TimeSpan.FromSeconds(30);

        public SiloStatisticsOptions()
        {
            this.PerfCountersWriteInterval = SILO_DEFAULT_PERF_COUNTERS_WRITE_PERIOD;
        }

        public TimeSpan DeploymentLoadPublisherRefreshTime { get; set; } = DEFAULT_DEPLOYMENT_LOAD_PUBLISHER_REFRESH_TIME;
        public static readonly TimeSpan DEFAULT_DEPLOYMENT_LOAD_PUBLISHER_REFRESH_TIME = TimeSpan.FromSeconds(1);


        /// <summary>
        /// The LoadShedding element specifies the gateway load shedding configuration for the node.
        /// If it does not appear, gateway load shedding is disabled.
        /// </summary>
        public bool LoadSheddingEnabled { get; set; }

        /// <summary>
        /// The LoadLimit attribute specifies the system load, in CPU%, at which load begins to be shed.
        /// Note that this value is in %, so valid values range from 1 to 100, and a reasonable value is
        /// typically between 80 and 95.
        /// This value is ignored if load shedding is disabled, which is the default.
        /// If load shedding is enabled and this attribute does not appear, then the default limit is 95%.
        /// </summary>
        public int LoadSheddingLimit { get; set; } = DEFAULT_LOAD_SHEDDING_LIMIT;
        public const int DEFAULT_LOAD_SHEDDING_LIMIT = 95;
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
                OptionFormattingUtilities.Format(nameof(this.options.DeploymentLoadPublisherRefreshTime), this.options.DeploymentLoadPublisherRefreshTime),
                OptionFormattingUtilities.Format(nameof(this.options.LoadSheddingEnabled), this.options.LoadSheddingEnabled),
                OptionFormattingUtilities.Format(nameof(this.options.LoadSheddingLimit), this.options.LoadSheddingLimit),
            });
            return format;
        }
    }
}
