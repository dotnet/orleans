using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration;

internal class MemoryPressureGrainCollectionOptionsValidator : IConfigurationValidator
{
    private readonly IOptions<GrainCollectionOptions> grainCollectionOptions;

    public MemoryPressureGrainCollectionOptionsValidator(IOptions<GrainCollectionOptions> options)
    {
        this.grainCollectionOptions = options;
    }

    public void ValidateConfiguration()
    {
        var options = grainCollectionOptions.Value.MemoryPressureGrainCollectionOptions;
        if (!options.MemoryUsageCollectionEnabled)
        {
            return;
        }

        if (options.MemoryUsagePollingPeriod <= TimeSpan.Zero)
        {
            throw new OrleansConfigurationException(
                $"{nameof(MemoryPressureGrainCollectionOptions.MemoryUsagePollingPeriod)} is set to {options.MemoryUsagePollingPeriod}. " +
                $"{nameof(MemoryPressureGrainCollectionOptions.MemoryUsagePollingPeriod)} must be greater than 0");
        }

        if (options.MemoryUsageLimitPercentage < 0 || options.MemoryUsageLimitPercentage > 100)
        {
            throw new OrleansConfigurationException(
                $"{nameof(MemoryPressureGrainCollectionOptions.MemoryUsageLimitPercentage)} is set to {options.MemoryUsageLimitPercentage}. " +
                $"{nameof(MemoryPressureGrainCollectionOptions.MemoryUsageLimitPercentage)} must be between 0 and 100");
        }

        if (options.MemoryUsageTargetPercentage < 0 || options.MemoryUsageTargetPercentage > 100)
        {
            throw new OrleansConfigurationException(
                $"{nameof(MemoryPressureGrainCollectionOptions.MemoryUsageTargetPercentage)} is set to {options.MemoryUsageTargetPercentage}. " +
                $"{nameof(MemoryPressureGrainCollectionOptions.MemoryUsageTargetPercentage)} must be between 0 and 100");
        }
        if (options.MemoryUsageTargetPercentage >= options.MemoryUsageLimitPercentage)
        {
            throw new OrleansConfigurationException(
                $"{nameof(MemoryPressureGrainCollectionOptions.MemoryUsageTargetPercentage)} is set to {options.MemoryUsageTargetPercentage}. " +
                $"{nameof(MemoryPressureGrainCollectionOptions.MemoryUsageTargetPercentage)} must be less than {nameof(MemoryPressureGrainCollectionOptions.MemoryUsageLimitPercentage)}, " +
                $"which is set to {options.MemoryUsageLimitPercentage}");
        }
    }
}
