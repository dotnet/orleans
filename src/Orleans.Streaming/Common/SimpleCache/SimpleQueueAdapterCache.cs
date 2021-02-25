using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Adapter for simple queue caches
    /// </summary>
    public class SimpleQueueAdapterCache : IQueueAdapterCache
    {
        /// <summary>
        /// Cache size property name for configuration
        /// </summary>
        public const string CacheSizePropertyName = "CacheSize";

        private readonly int cacheSize;
        private readonly string providerName;
        private readonly ILoggerFactory loggerFactory;

        /// <summary>
        /// Adapter for simple queue caches
        /// </summary>
        /// <param name="options"></param>
        /// <param name="providerName"></param>
        /// <param name="loggerFactory"></param>
        public SimpleQueueAdapterCache(SimpleQueueCacheOptions options, string providerName, ILoggerFactory loggerFactory)
        {
            this.cacheSize = options.CacheSize;
            this.loggerFactory = loggerFactory;
            this.providerName = providerName;
        }

        /// <summary>
        /// Create a cache for a given queue id
        /// </summary>
        /// <param name="queueId"></param>
        public IQueueCache CreateQueueCache(QueueId queueId)
        {
            return new SimpleQueueCache(cacheSize, this.loggerFactory.CreateLogger($"{typeof(SimpleQueueCache).FullName}.{providerName}.{queueId}"));
        }
    }
}
