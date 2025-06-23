using System;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    internal class GrainCollectionOptionsValidator : IConfigurationValidator
    {
        private readonly GrainCollectionOptions options;

        public GrainCollectionOptionsValidator(IOptions<GrainCollectionOptions> options)
        {
            this.options = options.Value;
        }

        public void ValidateConfiguration()
        {
            if (this.options.CollectionQuantum <= TimeSpan.Zero)
            {
                throw new OrleansConfigurationException(
                    $"{nameof(GrainCollectionOptions.CollectionQuantum)} is set to {options.CollectionQuantum}. " +
                    $"{nameof(GrainCollectionOptions.CollectionQuantum)} must be greater than 0");
            }

            if (this.options.CollectionAge <= this.options.CollectionQuantum)
            {
                throw new OrleansConfigurationException(
                    $"{nameof(GrainCollectionOptions.CollectionAge)} is set to {options.CollectionAge}. " +
                    $"{nameof(GrainCollectionOptions.CollectionAge)} must be greater than {nameof(GrainCollectionOptions.CollectionQuantum)}, " +
                    $"which is set to {this.options.CollectionQuantum}");
            }
            foreach(var classSpecificCollectionAge in this.options.ClassSpecificCollectionAge)
            {
                if (classSpecificCollectionAge.Value <= this.options.CollectionQuantum)
                {
                    throw new OrleansConfigurationException(
                        $"{classSpecificCollectionAge.Key} CollectionAgeLimit is set to {classSpecificCollectionAge.Value}. " +
                        $"CollectionAgeLimit must be greater than {nameof(GrainCollectionOptions.CollectionQuantum)}, " +
                        $"which is set to {this.options.CollectionQuantum}");
                }
            }

            ValidateHighMemoryPressureSettings();
        }

        private void ValidateHighMemoryPressureSettings()
        {
            if (!options.MemoryUsageCollectionEnabled)
            {
                return;
            }

            if (options.MemoryUsagePollingPeriod <= TimeSpan.Zero)
            {
                throw new OrleansConfigurationException(
                    $"{nameof(GrainCollectionOptions.MemoryUsagePollingPeriod)} is set to {options.MemoryUsagePollingPeriod}. " +
                    $"{nameof(GrainCollectionOptions.MemoryUsagePollingPeriod)} must be greater than 0");
            }

            if (options.MemoryUsageLimitPercentage < 0 || options.MemoryUsageLimitPercentage > 100)
            {
                throw new OrleansConfigurationException(
                    $"{nameof(GrainCollectionOptions.MemoryUsageLimitPercentage)} is set to {options.MemoryUsageLimitPercentage}. " +
                    $"{nameof(GrainCollectionOptions.MemoryUsageLimitPercentage)} must be between 0 and 100");
            }

            if (options.MemoryUsageTargetPercentage < 0 || options.MemoryUsageTargetPercentage > 100)
            {
                throw new OrleansConfigurationException(
                    $"{nameof(GrainCollectionOptions.MemoryUsageTargetPercentage)} is set to {options.MemoryUsageTargetPercentage}. " +
                    $"{nameof(GrainCollectionOptions.MemoryUsageTargetPercentage)} must be between 0 and 100");
            }
            if (options.MemoryUsageTargetPercentage >= options.MemoryUsageLimitPercentage)
            {
                throw new OrleansConfigurationException(
                    $"{nameof(GrainCollectionOptions.MemoryUsageTargetPercentage)} is set to {options.MemoryUsageTargetPercentage}. " +
                    $"{nameof(GrainCollectionOptions.MemoryUsageTargetPercentage)} must be less than {nameof(GrainCollectionOptions.MemoryUsageLimitPercentage)}, " +
                    $"which is set to {options.MemoryUsageLimitPercentage}");
            }
        }
    }
}
