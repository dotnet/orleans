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
        if (options.MetaStore is null)
        {
            throw new OrleansConfigurationException($"{prefix}.{nameof(options.MetaStore)} is required.");
        }

        if (options.HostId is not null && string.IsNullOrWhiteSpace(options.HostId))
        {
            throw new OrleansConfigurationException($"{prefix}.{nameof(options.HostId)} must not be empty.");
        }

        // With shared storage, silo addresses name silos with automatic hosting, which failover judges by membership.
        if (options.PartitionBlobContainer is not null && options.HostId is not null && Runtime.SiloAddress.TryParse(options.HostId, out _))
        {
            throw new OrleansConfigurationException(
                $"{prefix}.{nameof(options.HostId)} must not have the form of a silo address when {nameof(options.PartitionBlobContainer)} is set; leave it unset to host partitions under this silo's address.");
        }

        if (options.BucketCount <= 0 || !BitOperations.IsPow2(options.BucketCount))
        {
            throw new OrleansConfigurationException($"{prefix}.{nameof(options.BucketCount)} must be a positive power of two.");
        }

        if (options.InitialRangeMap is { } map)
        {
            try
            {
                map.Validate();
            }
            catch (InvalidOperationException exception)
            {
                throw new OrleansConfigurationException($"{prefix}.{nameof(options.InitialRangeMap)} is invalid: {exception.Message}", exception);
            }
        }

        if (options.CompactionThresholdBytes < 0)
        {
            throw new OrleansConfigurationException($"{prefix}.{nameof(options.CompactionThresholdBytes)} must be non-negative.");
        }

        if (options.OperationTimeout <= TimeSpan.Zero)
        {
            throw new OrleansConfigurationException($"{prefix}.{nameof(options.OperationTimeout)} must be positive.");
        }

        if (options.TombstoneRetention <= TimeSpan.Zero)
        {
            throw new OrleansConfigurationException($"{prefix}.{nameof(options.TombstoneRetention)} must be positive.");
        }

        if (options.MigrationInterval <= TimeSpan.Zero || options.MarkerCollectionInterval <= TimeSpan.Zero || options.HostingInterval <= TimeSpan.Zero)
        {
            throw new OrleansConfigurationException(
                $"{prefix}.{nameof(options.MigrationInterval)}, {nameof(options.MarkerCollectionInterval)}, and {nameof(options.HostingInterval)} must be positive.");
        }

        if (string.IsNullOrWhiteSpace(options.LogMemorySize) || string.IsNullOrWhiteSpace(options.IndexMemorySize) || string.IsNullOrWhiteSpace(options.CheckpointAofSize))
        {
            throw new OrleansConfigurationException(
                $"{prefix}.{nameof(options.LogMemorySize)}, {nameof(options.IndexMemorySize)}, and {nameof(options.CheckpointAofSize)} are required.");
        }
    }
}
