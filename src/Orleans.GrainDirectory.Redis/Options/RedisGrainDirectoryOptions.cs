using System;
using Orleans.GrainDirectory.Redis;
using Orleans.Runtime;
using StackExchange.Redis;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configuration options for the <see cref="RedisGrainDirectory"/>
    /// </summary>
    public class RedisGrainDirectoryOptions 
    {
        /// <summary>
        /// Configure the Redis client
        /// </summary>
        [RedactRedisConfigurationOptions]
        public ConfigurationOptions ConfigurationOptions { get; set; }

        /// <summary>
        /// Entry expiry, null by default. A value should be set ONLY for ephemeral environments (like in tests).
        /// Setting a value different from null will cause duplicate activations in the cluster.
        /// </summary>
        public TimeSpan? EntryExpiry { get; set; } = null;
    }

    public class RedactRedisConfigurationOptions : RedactAttribute
    {
        public override string Redact(object value) => value is ConfigurationOptions cfg ? cfg.ToString(includePassword: false) : base.Redact(value);
    }

    public class RedisGrainDirectoryOptionsValidator : IConfigurationValidator
    {
        private readonly RedisGrainDirectoryOptions options;

        public RedisGrainDirectoryOptionsValidator(RedisGrainDirectoryOptions options)
        {
            this.options = options;
        }

        public void ValidateConfiguration()
        {
            if (this.options.ConfigurationOptions == null)
            {
                throw new OrleansConfigurationException($"Invalid {nameof(RedisGrainDirectoryOptions)} values for {nameof(RedisGrainDirectory)}. {nameof(options.ConfigurationOptions)} is required.");
            }
        }
    }
}
