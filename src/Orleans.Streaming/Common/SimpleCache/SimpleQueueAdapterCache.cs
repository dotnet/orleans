using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Adapter for simple queue caches.
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
        /// Adapter for simple queue caches.
        /// </summary>
        /// <param name="options">The options.</param>
        /// <param name="providerName">The stream provider name.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        public SimpleQueueAdapterCache(SimpleQueueCacheOptions options, string providerName, ILoggerFactory loggerFactory)
        {
            this.cacheSize = options.CacheSize;
            this.loggerFactory = loggerFactory;
            this.providerName = providerName;
        }

        /// <inheritdoc />
        public IQueueCache CreateQueueCache(QueueId queueId)
        {
            return new SimpleQueueCache(cacheSize, this.loggerFactory.CreateLogger($"{typeof(SimpleQueueCache).FullName}.{providerName}.{queueId}"));
        }
    }
}
