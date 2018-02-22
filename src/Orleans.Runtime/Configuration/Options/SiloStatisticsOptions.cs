using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Orleans.Configuration
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
}
