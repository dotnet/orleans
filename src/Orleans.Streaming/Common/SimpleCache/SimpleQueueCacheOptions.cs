using System;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    public class SimpleQueueCacheOptions
    {
        public int CacheSize { get; set; } = DEFAULT_CACHE_SIZE;
        public const int DEFAULT_CACHE_SIZE = 4096;
    }

    public class SimpleQueueCacheOptionsValidator : IConfigurationValidator
    {
        private readonly SimpleQueueCacheOptions options;
        private readonly string name;

        private SimpleQueueCacheOptionsValidator(SimpleQueueCacheOptions options, string name)
        {
            this.options = options;
            this.name = name;
        }
        public void ValidateConfiguration()
        {
            if(options.CacheSize <= 0)
                throw new OrleansConfigurationException($"{nameof(SimpleQueueCacheOptions)} on stream provider {this.name} is invalid. {nameof(SimpleQueueCacheOptions.CacheSize)} must be larger than zero");
        }

        public static IConfigurationValidator Create(IServiceProvider services, string name)
        {
            SimpleQueueCacheOptions queueCacheOptions = services.GetOptionsByName<SimpleQueueCacheOptions>(name);
            return new SimpleQueueCacheOptionsValidator(queueCacheOptions, name);
        }
    }
}
