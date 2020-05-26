using System;
using Orleans.GrainDirectory.Redis;
using Orleans.Runtime;
using StackExchange.Redis;

namespace Orleans.Configuration
{
    public class RedisGrainDirectoryOptions 
    {
        [RedactRedisConfigurationOptions]
        public ConfigurationOptions ConfigurationOptions { get; set; }

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
            if (this.options == null)
            {
                throw new OrleansConfigurationException($"Invalid {nameof(RedisGrainDirectoryOptions)} values for {nameof(RedisGrainDirectory)}. {nameof(options.ConfigurationOptions)} is required.");
            }
        }
    }
}
