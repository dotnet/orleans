using System;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    public class RedisGrainDirectoryOptions 
    {
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
