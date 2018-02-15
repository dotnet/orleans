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
        private readonly IHostEnvironmentStatistics hostEnvironmentStatistics;
        private readonly float limit;

        public OverloadDetector(IHostEnvironmentStatistics hostEnvironmentStatistics, IOptions<LoadSheddingOptions> loadSheddingOptions)
        {
            this.hostEnvironmentStatistics = hostEnvironmentStatistics;
            this.Enabled = loadSheddingOptions.Value.LoadSheddingEnabled;
            this.limit = loadSheddingOptions.Value.LoadSheddingLimit;
            StringValueStatistic.FindOrCreate(StatisticNames.RUNTIME_IS_OVERLOADED, () => this.Overloaded.ToString());
        }

        /// <summary>
        /// Gets or sets a value indicating whether overload detection is enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Returns <see langword="true"/> if this process is overloaded, <see langword="false"/> otherwise.
        /// </summary>
        public bool Overloaded => this.Enabled && this.hostEnvironmentStatistics.CpuUsage > this.limit;
    }
}