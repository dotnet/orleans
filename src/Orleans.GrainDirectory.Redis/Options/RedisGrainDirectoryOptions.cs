using System;
using Orleans.Runtime;
using StackExchange.Redis;

namespace Orleans.Configuration
{
    public class RedisGrainDirectoryOptions 
    {
        [Redact]
        public ConfigurationOptions ConfigurationOptions { get; set; }
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
        }
    }
}
