using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Core.Messaging;
using Orleans.Statistics;

namespace Orleans.Runtime.Messaging
{
    /// <summary>
    /// Determines whether or not the process is overloaded.
    /// </summary>
    internal class OverloadDetector
    {
        private readonly IEnvironmentStatisticsProvider _environmentStatisticsProvider;
        private readonly LoadSheddingOptions _options;

        public OverloadDetector(IEnvironmentStatisticsProvider environmentStatisticsProvider, IOptions<LoadSheddingOptions> loadSheddingOptions)
        {
            _environmentStatisticsProvider = environmentStatisticsProvider;
            _options = loadSheddingOptions.Value;

            Enabled = _options.LoadSheddingEnabled;
        }

        /// <summary>
        /// Gets or sets a value indicating whether overload detection is enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Returns <see langword="true"/> if this process is overloaded, <see langword="false"/> otherwise.
        /// </summary>
        public bool IsOverloaded
        {
            get
            {
                if (!Enabled)
                    return false;
            
                var stats = _environmentStatisticsProvider.GetEnvironmentStatistics();
                return OverloadDetectionLogic.IsOverloaded(ref stats, _options);
            }
        }
    }
}