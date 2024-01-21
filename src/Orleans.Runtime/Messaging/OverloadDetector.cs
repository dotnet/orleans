using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Statistics;

namespace Orleans.Runtime.Messaging
{
    /// <summary>
    /// Determines whether or not the process is overloaded.
    /// </summary>
    internal class OverloadDetector
    {
        private readonly IEnvironmentStatistics environmentStatistics;
        private readonly float limit;

        public OverloadDetector(IEnvironmentStatistics environmentStatistics, IOptions<LoadSheddingOptions> loadSheddingOptions)
        {
            this.environmentStatistics = environmentStatistics;
            this.Enabled = loadSheddingOptions.Value.LoadSheddingEnabled;
            this.limit = loadSheddingOptions.Value.LoadSheddingLimit;
        }

        /// <summary>
        /// Gets or sets a value indicating whether overload detection is enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Returns <see langword="true"/> if this process is overloaded, <see langword="false"/> otherwise.
        /// </summary>
        public bool Overloaded => this.Enabled && (this.environmentStatistics.CpuUsagePercentage ?? 0) > this.limit;
    }
}