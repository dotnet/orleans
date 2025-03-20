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
    public ConfigurationOptions ConfigurationOptions { get; set; }

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
    /// The default multiplexer creation delegate.
    /// </summary>
    public static async Task<IConnectionMultiplexer> DefaultCreateMultiplexer(RedisStreamingOptions options) => await ConnectionMultiplexer.ConnectAsync(options.ConfigurationOptions);
}

internal sealed class RedactRedisConfigurationOptionsAttribute : RedactAttribute
{
    public override string Redact(object value) => value is ConfigurationOptions cfg ? cfg.ToString(includePassword: false) : base.Redact(value);
}
