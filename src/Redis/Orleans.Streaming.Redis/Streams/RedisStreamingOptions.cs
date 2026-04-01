using System;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Orleans.Configuration;

/// <summary>
/// Redis streaming options.
/// </summary>
public sealed class RedisStreamingOptions
{
    /// <summary>
    /// Gets or sets the Redis client options.
    /// </summary>
    [RedactRedisConfigurationOptionsAttribute]
    public ConfigurationOptions ConfigurationOptions { get; set; } = new();

    /// <summary>
    /// The delegate used to create a Redis connection multiplexer.
    /// </summary>
    public Func<RedisStreamingOptions, Task<IConnectionMultiplexer>> CreateMultiplexer { get; set; } = DefaultCreateMultiplexer;

    /// <summary>
    /// Entry expiry, null by default. A value should be set ONLY for ephemeral environments (like in tests).
    /// Setting a value different from null will cause reminder entries to be deleted after some period of time.
    /// </summary>
    public TimeSpan? EntryExpiry { get; set; } = null;

    /// <summary>
    /// Gets or sets the interval between checkpoint persistence attempts.
    /// </summary>
    public TimeSpan CheckpointPersistInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the maximum number of stream entries to retain.
    /// When null, Redis stream length is unbounded.
    /// </summary>
    public long? MaxStreamLength { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Redis should use approximate trimming when <see cref="MaxStreamLength"/> is configured.
    /// </summary>
    public bool UseApproximateMaxLength { get; set; } = true;

    /// <summary>
    /// The default multiplexer creation delegate.
    /// </summary>
    public static async Task<IConnectionMultiplexer> DefaultCreateMultiplexer(RedisStreamingOptions options) => await ConnectionMultiplexer.ConnectAsync(options.ConfigurationOptions);
}

internal sealed class RedactRedisConfigurationOptionsAttribute : RedactAttribute
{
    public override string Redact(object value) => value is ConfigurationOptions cfg ? cfg.ToString(includePassword: false) : base.Redact(value);
}
