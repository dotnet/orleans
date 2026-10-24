using System.Numerics;
using Orleans.Providers;

namespace Orleans.Journaling;

internal sealed class GarnetJournalStorageOptionsValidator(
    GarnetJournalStorageOptions options,
    string name = ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME) : IConfigurationValidator
{
    public void ValidateConfiguration()
    {
        ArgumentNullException.ThrowIfNull(options);
        var prefix = $"Invalid configuration for {nameof(GarnetJournalStorageProvider)} '{name}'. {nameof(GarnetJournalStorageOptions)}";
        var hasDirectory = !string.IsNullOrWhiteSpace(options.DataDirectory);
        if (hasDirectory == (options.ConfigurationOptions is not null))
        {
            throw new OrleansConfigurationException(
                $"{prefix}: set exactly one of {nameof(options.DataDirectory)} and {nameof(options.ConfigurationOptions)}.");
        }

        if (options.BucketCount <= 0 || !BitOperations.IsPow2(options.BucketCount))
        {
            throw new OrleansConfigurationException($"{prefix}.{nameof(options.BucketCount)} must be a positive power of two.");
        }

        if (options.CompactionThresholdBytes < 0)
        {
            throw new OrleansConfigurationException($"{prefix}.{nameof(options.CompactionThresholdBytes)} must be non-negative.");
        }

        if (options.OperationTimeout <= TimeSpan.Zero)
        {
            throw new OrleansConfigurationException($"{prefix}.{nameof(options.OperationTimeout)} must be positive.");
        }

        if (hasDirectory && (string.IsNullOrWhiteSpace(options.LogMemorySize) || string.IsNullOrWhiteSpace(options.IndexMemorySize) || string.IsNullOrWhiteSpace(options.CheckpointAofSize)))
        {
            throw new OrleansConfigurationException(
                $"{prefix}.{nameof(options.LogMemorySize)}, {nameof(options.IndexMemorySize)}, and {nameof(options.CheckpointAofSize)} are required for an embedded partition.");
        }
    }
}
