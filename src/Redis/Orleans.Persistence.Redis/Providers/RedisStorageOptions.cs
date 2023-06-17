#nullable enable
using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
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
        public bool DeleteStateOnClear { get; set; }

        /// <summary>
        /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialized prior to use.
        /// </summary>
        public int InitStage { get; set; } = ServiceLifecycleStage.ApplicationServices;

        /// <inheritdoc/>
        public IGrainStorageSerializer? GrainStorageSerializer { get; set; }

        /// <summary>
        /// Gets or sets the Redis client configuration.
        /// </summary>
        [RedactRedisConfigurationOptions]
        public ConfigurationOptions? ConfigurationOptions { get; set; }

        /// <summary>
        /// The delegate used to create a Redis connection multiplexer.
        /// </summary>
        public Func<RedisStorageOptions, Task<IConnectionMultiplexer>> CreateMultiplexer { get; set; } = DefaultCreateMultiplexer;

        /// <summary>
        /// Entry expiry, null by default. A value should be set only for ephemeral environments, such as testing environments.
        /// Setting a value different from <see langword="null"/> will cause duplicate activations in the cluster.
        /// </summary>
        public TimeSpan? EntryExpiry { get; set; } = null;

        /// <summary>
        /// Gets the Redis key for the provided grain type and grain identifier. If not set, the default implementation will be used, which is equivalent to <c>{ServiceId}/state/{grainId}/{grainType}</c>.
        /// </summary>
        public Func<string, GrainId, RedisKey>? GetStorageKey { get; set; }

        /// <summary>
        /// The default multiplexer creation delegate.
        /// </summary>
        public static async Task<IConnectionMultiplexer> DefaultCreateMultiplexer(RedisStorageOptions options) => await ConnectionMultiplexer.ConnectAsync(options.ConfigurationOptions!);
    }

    /// <summary>
    /// Extension methods for configuring <see cref="RedisStorageOptions"/>.
    /// </summary>
    public static class RedisStorageOptionsExtensions
    {
        /// <summary>
        /// Configures the provided options to use a Redis key format that ignores the grain type, equivalent to <c>{ServiceId}/state/{grainId}</c>.
        /// </summary>
        /// <remarks>
        /// This method is provided as a compatibility utility for users who are migrating from prerelease versions of the Redis storage provider.
        /// </remarks>
        /// <param name="optionsBuilder">The options builder.</param>
        public static void UseGetRedisKeyIgnoringGrainType(this OptionsBuilder<RedisStorageOptions> optionsBuilder)
        {
            optionsBuilder.Configure((RedisStorageOptions options, IOptions<ClusterOptions> clusterOptions) =>
            {
                RedisKey keyPrefix = Encoding.UTF8.GetBytes($"{clusterOptions.Value.ServiceId}/state/");
                options.GetStorageKey = (_, grainId) => keyPrefix.Append(grainId.ToString());
            });
        }
    }

    internal class RedactRedisConfigurationOptions : RedactAttribute
    {
        public override string Redact(object value) => value is ConfigurationOptions cfg ? cfg.ToString(includePassword: false) : base.Redact(value);
    }
}