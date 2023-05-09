using Microsoft.Extensions.Options;
using Orleans.Runtime;
using StackExchange.Redis;
using System;
using System.Threading.Tasks;

namespace Orleans.Clustering.Redis
{
    /// <summary>
    /// Options for Redis clustering.
    /// </summary>
    public class RedisClusteringOptions
    {
        /// <summary>
        /// Gets or sets the Redis client configuration.
        /// </summary>
        [RedactRedisConfigurationOptions]
        public ConfigurationOptions ConfigurationOptions { get; set; }

        /// <summary>
        /// The delegate used to create a Redis connection multiplexer.
        /// </summary>
        public Func<RedisClusteringOptions, Task<IConnectionMultiplexer>> CreateMultiplexer { get; set; } = DefaultCreateMultiplexer;

        /// <summary>
        /// Entry expiry, null by default. A value should be set ONLY for ephemeral environments (like in tests).
        /// Setting a value different from null will cause entries to be deleted after some period of time.
        /// </summary>
        public TimeSpan? EntryExpiry { get; set; } = null;

        /// <summary>
        /// The default multiplexer creation delegate.
        /// </summary>
        public static async Task<IConnectionMultiplexer> DefaultCreateMultiplexer(RedisClusteringOptions options)
        {
            return await ConnectionMultiplexer.ConnectAsync(options.ConfigurationOptions);
        }
    }

    internal class RedactRedisConfigurationOptions : RedactAttribute
    {
        public override string Redact(object value) => value is ConfigurationOptions cfg ? cfg.ToString(includePassword: false) : base.Redact(value);
    }

    /// <summary>
    /// Configuration validator for <see cref="RedisClusteringOptions"/>.
    /// </summary>
    public class RedisClusteringOptionsValidator : IConfigurationValidator
    {
        private readonly RedisClusteringOptions _options;

        public RedisClusteringOptionsValidator(IOptions<RedisClusteringOptions> options)
        {
            _options = options.Value;
        }

        /// <inheritdoc/>
        public void ValidateConfiguration()
        {
            if (_options.ConfigurationOptions == null)
            {
                throw new OrleansConfigurationException($"Invalid configuration for {nameof(RedisMembershipTable)}. {nameof(RedisClusteringOptions)}.{nameof(_options.ConfigurationOptions)} is required.");
            }
        }
    }
}