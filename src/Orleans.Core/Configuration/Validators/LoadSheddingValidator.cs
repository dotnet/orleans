using System;

using Microsoft.Extensions.DependencyInjection;
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
        private readonly IServiceProvider serviceProvider;

        internal const string InvalidLoadSheddingLimit = "LoadSheddingLimit cannot exceed 100%.";
        internal const string HostEnvironmentStatisticsNotConfigured = "A valid implementation of IHostEnvironmentStatistics is required for LoadShedding.";
        
        public LoadSheddingValidator(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        /// <inheritdoc />
        public void ValidateConfiguration()
        {
            // When Load Shedding is disabled, don't validate configuration.
            var options = this.serviceProvider.GetService<IOptions<LoadSheddingOptions>>();
            if (!options.Value.LoadSheddingEnabled)
            {
                return;
            }

            if (options.Value.LoadSheddingLimit > 100)
            {
                throw new OrleansConfigurationException(InvalidLoadSheddingLimit);
            }

            // With a provided LoadSheddingOptions, ensure there is a valid (non default) registered implementation of IHostEnvironmentStatistics.
            var hostEnvironmentStatistics = this.serviceProvider.GetService<IHostEnvironmentStatistics>();
            if (hostEnvironmentStatistics == null || hostEnvironmentStatistics.GetType() == typeof(NoOpHostEnvironmentStatistics))
            {
                throw new OrleansConfigurationException(HostEnvironmentStatisticsNotConfigured);
            }
        }
    }
}
