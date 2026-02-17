using StackExchange.Redis;

namespace Orleans.Hosting;

/// <summary>
/// Options for configuring the Redis durable jobs provider.
/// </summary>
public class RedisJobShardOptions
{
    /// <summary>
    /// Gets or sets the Redis client configuration.
    /// </summary>
    [RedactRedisConfigurationOptions]
    public ConfigurationOptions? ConfigurationOptions { get; set; }

    /// <summary>
    /// Gets or sets a delegate to create the Redis connection multiplexer.
    /// </summary>
    /// <remarks>
    /// This delegate is called once during initialization to create the connection.
    /// </remarks>
    public Func<RedisJobShardOptions, Task<IConnectionMultiplexer>> CreateMultiplexer { get; set; } = DefaultCreateMultiplexer;

    /// <summary>
    /// Gets or sets the prefix for Redis keys used by the durable jobs provider.
    /// </summary>
    /// <remarks>
    /// This prefix is combined with the service ID to create the final key prefix.
    /// If not set, defaults to null and the standard pattern is used: "{ServiceId}/durablejobs".
    /// </remarks>
    public string? KeyPrefix { get; set; }

    /// <summary>
    /// Gets or sets the prefix for shard identifiers.
    /// </summary>
    /// <remarks>
    /// This prefix is used to namespace shards in Redis, allowing multiple applications to share the same Redis instance.
    /// </remarks>
    public string ShardPrefix { get; set; } = "shard";

    /// <summary>
    /// Gets or sets the maximum number of retries when creating a shard in case of ID collisions.
    /// </summary>
    public int MaxShardCreationRetries { get; set; } = 5;

    /// <summary>
    /// Gets or sets the maximum number of job operations to batch together in a single write.
    /// Default is 128 operations.
    /// </summary>
    public int MaxBatchSize { get; set; } = 128;

    /// <summary>
    /// Gets or sets the minimum number of job operations to batch together before flushing.
    /// Default is 1 operation (immediate flush, optimized for latency).
    /// </summary>
    public int MinBatchSize { get; set; } = 1;

    /// <summary>
    /// Gets or sets the maximum time to wait for additional operations if the minimum batch size isn't reached
    /// before flushing a batch.
    /// Default is 100 milliseconds.
    /// </summary>
    public TimeSpan BatchFlushInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The default multiplexer creation delegate.
    /// </summary>
    public static async Task<IConnectionMultiplexer> DefaultCreateMultiplexer(RedisJobShardOptions options)
        => await ConnectionMultiplexer.ConnectAsync(options.ConfigurationOptions!);
}

internal class RedactRedisConfigurationOptions : RedactAttribute
{
    public override string Redact(object value) => value is ConfigurationOptions cfg ? cfg.ToString(includePassword: false) : base.Redact(value);
}
