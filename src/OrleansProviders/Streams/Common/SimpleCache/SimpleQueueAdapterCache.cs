using System;
using System.Collections.Concurrent;
using Orleans.Runtime;
using Orleans.Streams;

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
        private readonly Logger logger;
        private readonly ConcurrentDictionary<QueueId, IQueueCache> caches;

        /// <summary>
        /// Adapter for simple queue caches
        /// </summary>
        /// <param name="cacheSize"></param>
        /// <param name="logger"></param>
        public SimpleQueueAdapterCache(int cacheSize, Logger logger)
        {
            if (cacheSize <= 0)
                throw new ArgumentOutOfRangeException("cacheSize", "CacheSize must be a positive number.");
            this.cacheSize = cacheSize;
            this.logger = logger;
            caches = new ConcurrentDictionary<QueueId, IQueueCache>();
        }

        /// <summary>
        /// Create a cache for a given queue id
        /// </summary>
        /// <param name="queueId"></param>
        public IQueueCache CreateQueueCache(QueueId queueId)
        {
            return caches.AddOrUpdate(queueId, (id) => new SimpleQueueCache(cacheSize, logger), (id, queueCache) => queueCache);
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
