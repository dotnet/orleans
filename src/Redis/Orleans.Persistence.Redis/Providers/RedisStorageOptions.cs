using System;
using System.Threading.Tasks;
using Orleans.Storage;
using StackExchange.Redis;

namespace Orleans.Persistence
{
    /// <summary>
    /// Redis grain storage options.
    /// </summary>
    public class RedisStorageOptions : IStorageProviderSerializerOptions
    {
        /// <summary>
        /// Whether or not to delete state during a clear operation.
        /// </summary>
        public bool DeleteOnClear { get; set; }

        /// <summary>
        /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialized prior to use.
        /// </summary>
        public int InitStage { get; set; } = ServiceLifecycleStage.ApplicationServices;

        /// <inheritdoc/>
        public IGrainStorageSerializer GrainStorageSerializer { get; set; }

        /// <summary>
        /// Gets or sets the Redis client configuration.
        /// </summary>
        [RedactRedisConfigurationOptions]
        public ConfigurationOptions ConfigurationOptions { get; set; }

        /// <summary>
        /// The delegate used to create a Redis connection multiplexer.
        /// </summary>
        public Func<RedisStorageOptions, Task<IConnectionMultiplexer>> CreateMultiplexer { get; set; } = DefaultCreateMultiplexer;

        /// <summary>
        /// Entry expiry, null by default. A value should be set ONLY for ephemeral environments (like in tests).
        /// Setting a value different from null will cause duplicate activations in the cluster.
        /// </summary>
        public TimeSpan? EntryExpiry { get; set; } = null;

        /// <summary>
        /// The default multiplexer creation delegate.
        /// </summary>
        public static async Task<IConnectionMultiplexer> DefaultCreateMultiplexer(RedisStorageOptions options) => await ConnectionMultiplexer.ConnectAsync(options.ConfigurationOptions);
    }

    internal class RedactRedisConfigurationOptions : RedactAttribute
    {
        public override string Redact(object value) => value is ConfigurationOptions cfg ? cfg.ToString(includePassword: false) : base.Redact(value);
    }
}