using System;
using System.Collections.Concurrent;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    public class SimpleQueueAdapterCache : IQueueAdapterCache
    {
        private const string CACHE_SIZE_PARAM = "CacheSize";

        private readonly int cacheSize;
        private readonly Logger logger;
        private readonly ConcurrentDictionary<QueueId, IQueueCache> caches;
        
        public SimpleQueueAdapterCache(int cacheSize, Logger logger)
        {
            if (cacheSize <= 0)
                throw new ArgumentOutOfRangeException("cacheSize", "CacheSize must be a positive number.");
            this.cacheSize = cacheSize;
            this.logger = logger;
            caches = new ConcurrentDictionary<QueueId, IQueueCache>();
        }

        public IQueueCache CreateQueueCache(QueueId queueId)
        {
            return caches.AddOrUpdate(queueId, (id) => new SimpleQueueCache(cacheSize, logger), (id, queueCache) => queueCache);
        }

        public static int ParseSize(IProviderConfiguration config, int defaultSize)
        {
            return config.GetIntProperty(CACHE_SIZE_PARAM, defaultSize);
        }
    }
}
