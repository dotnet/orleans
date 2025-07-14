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
        private const int RefreshIntervalMilliseconds = 1_000;
        private readonly IEnvironmentStatisticsProvider _environmentStatisticsProvider;
        private readonly LoadSheddingOptions _options;
        private CoarseStopwatch _refreshStopwatch;
        private bool? _isOverloaded;

        public OverloadDetector(IEnvironmentStatisticsProvider environmentStatisticsProvider, IOptions<LoadSheddingOptions> loadSheddingOptions)
        {
            _environmentStatisticsProvider = environmentStatisticsProvider;
            _options = loadSheddingOptions.Value;

            Enabled = _options.LoadSheddingEnabled;

            _refreshStopwatch = CoarseStopwatch.StartNew();
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
                {
                    return false;
                }

                if (!_isOverloaded.HasValue || _refreshStopwatch.ElapsedMilliseconds >= RefreshIntervalMilliseconds)
                {
                    var stats = _environmentStatisticsProvider.GetEnvironmentStatistics();
                    _isOverloaded = OverloadDetectionLogic.IsOverloaded(ref stats, _options);
                    _refreshStopwatch.Restart();
                }

                return _isOverloaded.Value;
            }
        }

        // Exposed only for testing purposes
        internal void ForceRefresh()
        {
            _isOverloaded = null;
        }
    }
}
