using Microsoft.Extensions.Options;
using Orleans.Runtime;
using StackExchange.Redis;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading.Tasks;
using Orleans.Configuration;

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
        public ConfigurationOptions? ConfigurationOptions { get; set; }

        /// <summary>
        /// The delegate used to create a Redis connection multiplexer and indicate whether it is shared.
        /// </summary>
        /// <remarks>
        /// When <c>IsShared</c> is <see langword="true"/>, the provider will not dispose the returned multiplexer.
        /// </remarks>
        public Func<RedisClusteringOptions, Task<(IConnectionMultiplexer Multiplexer, bool IsShared)>> CreateMultiplexer { get; set; } = DefaultCreateMultiplexer;

        /// <summary>
        /// The delegate used to create redis key for RedisMembershipTable.
        /// </summary>
        public Func<ClusterOptions, RedisKey> CreateRedisKey { get; set; } = DefaultCreateRedisKey;

        /// <summary>
        /// Gets or sets the expiry for the cluster's membership hash, refreshed when initializing its table version.
        /// The default is <see langword="null"/>. Configure an expiry for ephemeral environments, such as tests.
        /// Expiration removes the table version and every member; subsequent membership reads report the missing table.
        /// </summary>
        public TimeSpan? EntryExpiry { get; set; } = null;

        /// <summary>
        /// The default multiplexer creation delegate.
        /// </summary>
        /// <param name="options">The Redis clustering options.</param>
        /// <returns>A task containing the created multiplexer and a value indicating whether it is shared.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
        public static async Task<(IConnectionMultiplexer Multiplexer, bool IsShared)> DefaultCreateMultiplexer(RedisClusteringOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return (await ConnectionMultiplexer.ConnectAsync(options.ConfigurationOptions!), false);
        }

        /// <summary>
        /// Creates the default Redis key for <see cref="RedisMembershipTable"/>.
        /// </summary>
        /// <param name="clusterOptions">The cluster identity options.</param>
        /// <returns>The Redis membership table key.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="clusterOptions"/> is <see langword="null"/>.</exception>
        public static RedisKey DefaultCreateRedisKey(ClusterOptions clusterOptions)
        {
            ArgumentNullException.ThrowIfNull(clusterOptions);
            return Encoding.UTF8.GetBytes($"{clusterOptions.ServiceId}/members/{clusterOptions.ClusterId}");
        }
    }

    internal class RedactRedisConfigurationOptions : RedactAttribute
    {
        public override string Redact(object? value) => value is ConfigurationOptions cfg ? cfg.ToString(includePassword: false) : base.Redact(value);
    }

    /// <summary>
    /// Configuration validator for <see cref="RedisClusteringOptions"/>.
    /// </summary>
    public class RedisClusteringOptionsValidator : IConfigurationValidator
    {
        private readonly RedisClusteringOptions _options;

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisClusteringOptionsValidator"/> class.
        /// </summary>
        /// <param name="options">The options to validate.</param>
        [SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "Microsoft.Extensions.DependencyInjection supplies the resolved options instance.")]
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
