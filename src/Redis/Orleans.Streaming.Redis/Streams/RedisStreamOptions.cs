using System.Text;
using Orleans.Streams;
using StackExchange.Redis;

namespace Orleans.Configuration;

/// <summary>
/// Options for Redis streaming.
/// </summary>
public class RedisStreamOptions
{
    /// <summary>
    /// Gets or sets the Redis client configuration.
    /// </summary>
    [RedactRedisConfigurationOptions]
    public ConfigurationOptions ConfigurationOptions { get; set; } = default!;

    /// <summary>
    /// The delegate used to create a Redis connection multiplexer.
    /// </summary>
    public Func<RedisStreamOptions, Task<IConnectionMultiplexer>> CreateMultiplexer { get; set; } = DefaultCreateMultiplexer;

    /// <summary>
    /// Gets the Redis key for the provided QueueId. If not set, the default implementation will be used, which is equivalent to <c>{ServiceId}/streams/{queueId}</c>.
    /// </summary>
    public Func<ClusterOptions, QueueId, RedisKey> GetRedisKey { get; set; } = DefaultGetRedisKey;

    /// <summary>
    /// Entry expiry, null by default. A value should be set ONLY for ephemeral environments (like in tests).
    /// Setting a value different from null will cause stream entries to be deleted after some period of time.
    /// </summary>
    public TimeSpan? EntryExpiry { get; set; }

    /// <summary>
    /// The default multiplexer creation delegate.
    /// </summary>
    public static async Task<IConnectionMultiplexer> DefaultCreateMultiplexer(RedisStreamOptions options) =>
        await ConnectionMultiplexer.ConnectAsync(options.ConfigurationOptions);

    /// <summary>
    /// The default redis key delegate.
    /// </summary>        
    private static RedisKey DefaultGetRedisKey(ClusterOptions clusterOptions, QueueId queueId)
    {
        RedisKey key = Encoding.UTF8.GetBytes($"{clusterOptions.ServiceId}/streaming/{queueId}");
        return key;
    }
}

internal class RedactRedisConfigurationOptions : RedactAttribute
{
    public override string Redact(object value) => value is ConfigurationOptions cfg ? cfg.ToString(includePassword: false) : base.Redact(value);
}
