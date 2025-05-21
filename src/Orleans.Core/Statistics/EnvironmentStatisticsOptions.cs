using System;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configuration options for environment statistics.
    /// </summary>
    public class EnvironmentStatisticsOptions
    {
        /// <summary>
        /// Gets or sets the interval at which CPU usage is collected.
        /// </summary>
        public TimeSpan CPUUsageCollectionInterval { get; set; } = DefaultCPUUsageCollectionInterval;

        /// <summary>
        /// The default value for <see cref="CPUUsageCollectionInterval"/>.
        /// </summary>
        public static readonly TimeSpan DefaultCPUUsageCollectionInterval = TimeSpan.FromSeconds(1);
    }
}