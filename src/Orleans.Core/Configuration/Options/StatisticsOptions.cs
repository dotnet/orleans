using System;
using Orleans.Runtime.Configuration;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for configuring statistics collection and logging.
    /// </summary>
    public class StatisticsOptions
    {
        /// <summary>
        /// Gets or sets the period of time between publishing statistics.
        /// </summary>
        /// <value>Statistics values are published every 30 seconds by default.</value>
        public TimeSpan PerfCountersWriteInterval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the period of time between logging statistics
        /// </summary>
        /// <value>Statistics are logged every 5 minutes by default.</value>
        public TimeSpan LogWriteInterval { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Gets or sets the statistics collection level.
        /// </summary>
        /// <value>
        /// Statistics are logged at collected at the <see cref="StatisticsLevel.Info"/> level by default.
        /// </value>
        public StatisticsLevel CollectionLevel { get; set; } = StatisticsLevel.Info;
    }
}
