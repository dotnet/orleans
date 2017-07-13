using System;
using Orleans.Runtime;
using Orleans.Providers.Streams.Common;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Eviction strategy for EventHubQueueCache
    /// </summary>
    public class EventHubCacheEvictionStrategy : ChronologicalEvictionStrategy<CachedEventHubMessage>
    {
        private static readonly string LogName = typeof(EventHubCacheEvictionStrategy).Namespace;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="cacheMonitor"></param>
        /// <param name="monitorWriteInterval"></param>
        /// <param name="timePurage"></param>
        public EventHubCacheEvictionStrategy(Logger logger, TimePurgePredicate timePurage, ICacheMonitor cacheMonitor, TimeSpan? monitorWriteInterval)
            : base(logger.GetLogger(LogName), timePurage, cacheMonitor, monitorWriteInterval)
        {
        }

        /// <summary>
        /// Get block pool block id for message
        /// </summary>
        /// <param name="cachedMessage"></param>
        /// <returns></returns>
        protected override object GetBlockId(CachedEventHubMessage? cachedMessage)
        {
            return cachedMessage.HasValue ? cachedMessage.Value.Segment.Array : null;
        }

        /// <summary>
        /// Get message dequeue time
        /// </summary>
        /// <param name="cachedMessage"></param>
        /// <returns></returns>
        protected override DateTime GetDequeueTimeUtc(ref CachedEventHubMessage cachedMessage)
        {
            return cachedMessage.DequeueTimeUtc;
        }

        /// <summary>
        /// Get message enqueue time
        /// </summary>
        /// <param name="cachedMessage"></param>
        /// <returns></returns>
        protected override  DateTime GetEnqueueTimeUtc(ref CachedEventHubMessage cachedMessage)
        {
            return cachedMessage.EnqueueTimeUtc;
        }
    }
}
