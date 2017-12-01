using Microsoft.Extensions.Logging;
using Orleans.Streams;
using System;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Adapter for simple queue caches
    /// </summary>
    public class SimpleQueueAdapterCache : IQueueAdapterCache
    {
        /// <summary>
        /// Cache size propery name for configuration
        /// </summary>
        public const string CacheSizePropertyName = "CacheSize";

        private readonly int cacheSize;
        private readonly string providerName;
        private readonly ILoggerFactory loggerFactory;
        /// <summary>
        /// Adapter for simple queue caches
        /// </summary>
        /// <param name="cacheSize"></param>
        /// <param name="providerName"></param>
        /// <param name="loggerFactory"></param>
        public SimpleQueueAdapterCache(int cacheSize, string providerName, ILoggerFactory loggerFactory)
        {
            if (cacheSize <= 0)
                throw new ArgumentOutOfRangeException("cacheSize", "CacheSize must be a positive number.");
            this.cacheSize = cacheSize;
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

        /// <summary>
        /// Parce the size property from configuration
        /// </summary>
        /// <param name="config"></param>
        /// <param name="defaultSize"></param>
        /// <returns></returns>
        public static int ParseSize(IProviderConfiguration config, int defaultSize)
        {
            return config.GetIntProperty(CacheSizePropertyName, defaultSize);
        }
    }
}
