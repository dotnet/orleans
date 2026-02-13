using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Core.Messaging;
using Orleans.Statistics;

namespace Orleans.Runtime.Messaging
{
    /// <summary>
    /// Determines whether or not the process is overloaded.
    /// </summary>
    public interface IOverloadDetector
    {
        /// <summary>
        /// Returns <see langword="true"/> if this process is overloaded, <see langword="false"/> otherwise.
        /// </summary>
        bool IsOverloaded { get; }
    }

    /// <summary>
    /// Determines whether or not the process is overloaded.
    /// </summary>
    internal class OverloadDetector : IOverloadDetector
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);
        private readonly IEnvironmentStatisticsProvider _environmentStatisticsProvider;
        private readonly LoadSheddingOptions _options;
        private readonly TimeProvider _timeProvider;
        private long _lastRefreshTimestamp;
        private bool? _isOverloaded;

        public OverloadDetector(
            IEnvironmentStatisticsProvider environmentStatisticsProvider,
            IOptions<LoadSheddingOptions> loadSheddingOptions,
            TimeProvider timeProvider = null)
        {
            _environmentStatisticsProvider = environmentStatisticsProvider;
            _options = loadSheddingOptions.Value;
            _timeProvider = timeProvider ?? TimeProvider.System;

            Enabled = _options.LoadSheddingEnabled;

            _lastRefreshTimestamp = _timeProvider.GetTimestamp();
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

                var currentTimestamp = _timeProvider.GetTimestamp();
                var elapsed = _timeProvider.GetElapsedTime(_lastRefreshTimestamp, currentTimestamp);

                if (!_isOverloaded.HasValue || elapsed >= RefreshInterval)
                {
                    var stats = _environmentStatisticsProvider.GetEnvironmentStatistics();
                    _isOverloaded = OverloadDetectionLogic.IsOverloaded(ref stats, _options);
                    _lastRefreshTimestamp = currentTimestamp;
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
