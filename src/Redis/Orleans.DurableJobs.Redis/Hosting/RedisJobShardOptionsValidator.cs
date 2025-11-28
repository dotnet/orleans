using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration.Internal;
using Orleans.Runtime;

namespace Orleans.Hosting;

/// <summary>
/// Validates <see cref="RedisJobShardOptions"/>.
/// </summary>
public class RedisJobShardOptionsValidator : IConfigurationValidator
{
    private readonly RedisJobShardOptions _options;
    private readonly string _name;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisJobShardOptionsValidator"/> class.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <param name="name">The name.</param>
    public RedisJobShardOptionsValidator(RedisJobShardOptions options, string name)
    {
        _options = options;
        _name = name;
    }

    /// <inheritdoc/>
    public void ValidateConfiguration()
    {
        if (_options.ConfigurationOptions is null)
        {
            throw new OrleansConfigurationException($"Invalid configuration for {nameof(RedisJobShardOptions)} with name '{_name}'. {nameof(_options.ConfigurationOptions)} is required.");
        }

        if (string.IsNullOrWhiteSpace(_options.ShardPrefix))
        {
            throw new OrleansConfigurationException($"Invalid configuration for {nameof(RedisJobShardOptions)} with name '{_name}'. {nameof(_options.ShardPrefix)} is required.");
        }

        if (_options.MaxShardCreationRetries < 1)
        {
            throw new OrleansConfigurationException($"Invalid configuration for {nameof(RedisJobShardOptions)} with name '{_name}'. {nameof(_options.MaxShardCreationRetries)} must be at least 1.");
        }

        if (_options.MaxBatchSize < 1)
        {
            throw new OrleansConfigurationException($"Invalid configuration for {nameof(RedisJobShardOptions)} with name '{_name}'. {nameof(_options.MaxBatchSize)} must be at least 1.");
        }

        if (_options.MinBatchSize < 1)
        {
            throw new OrleansConfigurationException($"Invalid configuration for {nameof(RedisJobShardOptions)} with name '{_name}'. {nameof(_options.MinBatchSize)} must be at least 1.");
        }

        if (_options.MinBatchSize > _options.MaxBatchSize)
        {
            throw new OrleansConfigurationException($"Invalid configuration for {nameof(RedisJobShardOptions)} with name '{_name}'. {nameof(_options.MinBatchSize)} must not exceed {nameof(_options.MaxBatchSize)}.");
        }

        if (_options.BatchFlushInterval < TimeSpan.Zero)
        {
            throw new OrleansConfigurationException($"Invalid configuration for {nameof(RedisJobShardOptions)} with name '{_name}'. {nameof(_options.BatchFlushInterval)} must not be negative.");
        }
    }
}
