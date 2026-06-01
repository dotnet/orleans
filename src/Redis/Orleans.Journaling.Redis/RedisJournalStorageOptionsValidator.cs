using Orleans.Runtime;
using StackExchange.Redis;

namespace Orleans.Journaling;

internal sealed class RedisJournalStorageOptionsValidator(RedisJournalStorageOptions options) : IConfigurationValidator
{
    private static readonly Func<RedisJournalStorageOptions, Task<(IConnectionMultiplexer Multiplexer, bool IsShared)>> DefaultCreateMultiplexer
        = RedisJournalStorageOptions.DefaultCreateMultiplexer;

    public void ValidateConfiguration()
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.CreateMultiplexer is null)
        {
            throw new OrleansConfigurationException(
                $"Invalid configuration for {nameof(RedisJournalStorageProvider)}. {nameof(RedisJournalStorageOptions)}.{nameof(options.CreateMultiplexer)} is required.");
        }

        if (options.ConfigurationOptions is null && options.CreateMultiplexer.Equals(DefaultCreateMultiplexer))
        {
            throw new OrleansConfigurationException(
                $"Invalid configuration for {nameof(RedisJournalStorageProvider)}. {nameof(RedisJournalStorageOptions)}.{nameof(options.ConfigurationOptions)} is required when using the default multiplexer factory.");
        }

        if (options.GetKeyName is null)
        {
            throw new OrleansConfigurationException(
                $"Invalid configuration for {nameof(RedisJournalStorageProvider)}. {nameof(RedisJournalStorageOptions)}.{nameof(options.GetKeyName)} is required.");
        }

        if (options.CompactionThresholdBytes < 0)
        {
            throw new OrleansConfigurationException(
                $"Invalid configuration for {nameof(RedisJournalStorageProvider)}. {nameof(RedisJournalStorageOptions)}.{nameof(options.CompactionThresholdBytes)} must be non-negative.");
        }

        if (options.ReadChunkSize <= 0)
        {
            throw new OrleansConfigurationException(
                $"Invalid configuration for {nameof(RedisJournalStorageProvider)}. {nameof(RedisJournalStorageOptions)}.{nameof(options.ReadChunkSize)} must be positive.");
        }
    }
}
