﻿using System;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Providers.Streams.Common;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Eviction strategy for EventHubQueueCache
    /// </summary>
    public class EventHubCacheEvictionStrategy : ChronologicalEvictionStrategy<CachedEventHubMessage>
    {
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="timePurage"></param>
        /// <param name="cacheMonitor"></param>
        /// <param name="monitorWriteInterval">monitor write interval.  Only triggered for active caches.</param>
        public EventHubCacheEvictionStrategy(ILogger logger, TimePurgePredicate timePurage, ICacheMonitor cacheMonitor, TimeSpan? monitorWriteInterval)
            : base(logger, timePurage, cacheMonitor, monitorWriteInterval)
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
