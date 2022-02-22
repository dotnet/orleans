using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Statistics;

namespace Orleans.Configuration.Validators
{
    /// <summary>
    /// Validates <see cref="LoadSheddingOptions"/> configuration.
    /// </summary>
    internal class LoadSheddingValidator : IConfigurationValidator
    {
        private readonly LoadSheddingOptions _loadSheddingOptions;
        private readonly IHostEnvironmentStatistics _hostEnvironmentStatistics;

        internal const string InvalidLoadSheddingLimit = "LoadSheddingLimit cannot exceed 100%.";
        internal const string HostEnvironmentStatisticsNotConfigured = "A valid implementation of IHostEnvironmentStatistics is required for LoadShedding.";

        /// <summary>
        /// Initializes a new instance of the <see cref="LoadSheddingValidator"/> class.
        /// </summary>
        /// <param name="loadSheddingOptions">
        /// The load shedding options.
        /// </param>
        /// <param name="hostEnvironmentStatistics">
        /// The host environment statistics.
        /// </param>
        public LoadSheddingValidator(
            IOptions<LoadSheddingOptions> loadSheddingOptions,
            IHostEnvironmentStatistics hostEnvironmentStatistics
        )
        {
            _loadSheddingOptions = loadSheddingOptions.Value;
            _hostEnvironmentStatistics = hostEnvironmentStatistics;
        }

        /// <inheritdoc />
        public void ValidateConfiguration()
        {
            // When Load Shedding is disabled, don't validate configuration.
            if (!_loadSheddingOptions.LoadSheddingEnabled)
            {
                return;
            }

            if (_loadSheddingOptions.LoadSheddingLimit > 100)
            {
                throw new OrleansConfigurationException(InvalidLoadSheddingLimit);
            }

            // With a provided LoadSheddingOptions, ensure there is a valid (non default) registered implementation of IHostEnvironmentStatistics.
            if (_hostEnvironmentStatistics == null || _hostEnvironmentStatistics.GetType() == typeof(NoOpHostEnvironmentStatistics))
            {
                throw new OrleansConfigurationException(HostEnvironmentStatisticsNotConfigured);
            }
        }
    }
}
