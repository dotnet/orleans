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
        private readonly LoadSheddingOptions loadSheddingOptions;
        private readonly IHostEnvironmentStatistics hostEnvironmentStatistics;

        internal const string InvalidLoadSheddingLimit = "LoadSheddingLimit cannot exceed 100%.";
        internal const string HostEnvironmentStatisticsNotConfigured = "A valid implementation of IHostEnvironmentStatistics is required for LoadShedding.";
        
        public LoadSheddingValidator(
            IOptions<LoadSheddingOptions> loadSheddingOptions,
            IHostEnvironmentStatistics hostEnvironmentStatistics
        )
        {
            this.loadSheddingOptions = loadSheddingOptions.Value;
            this.hostEnvironmentStatistics = hostEnvironmentStatistics;
        }

        /// <inheritdoc />
        public void ValidateConfiguration()
        {
            // When Load Shedding is disabled, don't validate configuration.
            if (!loadSheddingOptions.LoadSheddingEnabled)
            {
                return;
            }

            if (loadSheddingOptions.LoadSheddingLimit > 100)
            {
                throw new OrleansConfigurationException(InvalidLoadSheddingLimit);
            }

            // With a provided LoadSheddingOptions, ensure there is a valid (non default) registered implementation of IHostEnvironmentStatistics.
            if (hostEnvironmentStatistics == null || hostEnvironmentStatistics.GetType() == typeof(NoOpHostEnvironmentStatistics))
            {
                throw new OrleansConfigurationException(HostEnvironmentStatisticsNotConfigured);
            }
        }
    }
}
