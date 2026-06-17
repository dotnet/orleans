using System;
using System.Threading.Tasks;
using Orleans.GrainDirectory.Redis;
using Orleans.Runtime;
using StackExchange.Redis;

#nullable disable
namespace Orleans.Configuration
{
    /// <summary>
    /// Configuration options for the <see cref="RedisGrainDirectory"/>
    /// </summary>
    public class RedisGrainDirectoryOptions 
    {
        /// <summary>
        /// Gets or sets the Redis client configuration.
        /// </summary>
        [RedactRedisConfigurationOptions]
        public ConfigurationOptions ConfigurationOptions { get; set; }

        /// <summary>
        /// The delegate used to create a Redis connection multiplexer and indicate whether it is shared.
        /// </summary>
        /// <remarks>
        /// When <c>IsShared</c> is <see langword="true"/>, the provider will not dispose the returned multiplexer.
        /// </remarks>
        public Func<RedisGrainDirectoryOptions, Task<(IConnectionMultiplexer Multiplexer, bool IsShared)>> CreateMultiplexer { get; set; } = DefaultCreateMultiplexer;

        /// <summary>
        /// Entry expiry, null by default. A value should be set ONLY for ephemeral environments (like in tests).
        /// Setting a value different from null will cause duplicate activations in the cluster.
        /// </summary>
        public TimeSpan? EntryExpiry { get; set; } = null;

        /// <summary>
        /// The default multiplexer creation delegate.
        /// </summary>
        public static async Task<(IConnectionMultiplexer Multiplexer, bool IsShared)> DefaultCreateMultiplexer(RedisGrainDirectoryOptions options)
            => (Multiplexer: await ConnectionMultiplexer.ConnectAsync(options.ConfigurationOptions), IsShared: false);
    }

    internal class RedactRedisConfigurationOptions : RedactAttribute
    {
        public override string Redact(object value) => value is ConfigurationOptions cfg ? cfg.ToString(includePassword: false) : base.Redact(value);
    }

    /// <summary>
    /// Configuration validator for <see cref="RedisGrainDirectoryOptions"/>.
    /// </summary>
    public class RedisGrainDirectoryOptionsValidator : IConfigurationValidator
    {
        private readonly RedisGrainDirectoryOptions _options;
        private readonly string _name;

        public RedisGrainDirectoryOptionsValidator(RedisGrainDirectoryOptions options, string name)
        {
            _options = options;
            _name = name;
        }

        /// <inheritdoc/>
        public void ValidateConfiguration()
        {
            if (_options.ConfigurationOptions == null)
            {
                throw new OrleansConfigurationException($"Invalid configuration for {nameof(RedisGrainDirectory)} with name {_name}. {nameof(RedisGrainDirectoryOptions)}.{nameof(_options.ConfigurationOptions)} is required.");
            }
        }
    }
}
