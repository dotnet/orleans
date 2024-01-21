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
        private readonly IEnvironmentStatistics _environmentStatistics;
        private readonly LoadSheddingOptions _options;

        public OverloadDetector(IEnvironmentStatistics environmentStatistics, IOptions<LoadSheddingOptions> loadSheddingOptions)
        {
            _environmentStatistics = environmentStatistics;
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
        public bool IsOverloaded => Enabled && OverloadDetectionLogic.Determine(_environmentStatistics, _options);
    }
}