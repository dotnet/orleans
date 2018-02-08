using Microsoft.Extensions.Options;

using Orleans.Hosting;
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
        private readonly bool isEnabled;

        public OverloadDetector(IHostEnvironmentStatistics hostEnvironmentStatistics, IOptions<LoadSheddingOptions> loadSheddingOptions)
        {
            this.hostEnvironmentStatistics = hostEnvironmentStatistics;
            this.isEnabled = loadSheddingOptions.Value.LoadSheddingEnabled;
            this.limit = (float)loadSheddingOptions.Value.LoadSheddingLimit;
            StringValueStatistic.FindOrCreate(StatisticNames.RUNTIME_IS_OVERLOADED, () => this.IsOverloaded.ToString());
        }

        /// <summary>
        /// Returns <see langword="true"/> if this process is overloaded, <see langword="false"/> otherwise.
        /// </summary>
        public bool IsOverloaded => this.isEnabled && this.hostEnvironmentStatistics.CpuUsage > this.limit;
    }
}