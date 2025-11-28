using System;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Orleans.Hosting;

/// <summary>
/// Options for configuring the Redis durable jobs provider.
/// </summary>
public class RedisJobShardOptions
{
    /// <summary>
    /// Gets or sets a delegate to create the Redis connection multiplexer.
    /// </summary>
    /// <remarks>
    /// This delegate is called once during initialization to create the connection.
    /// </remarks>
    public Func<CancellationToken, Task<IConnectionMultiplexer>> CreateMultiplexer { get; set; } = _ =>
        throw new InvalidOperationException($"Configuration is required for Redis durable jobs. Set the {nameof(CreateMultiplexer)} delegate.");

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
}
