using System;
using Orleans.Streams;

namespace Orleans.Configuration
{
    public class StreamCacheEvictionOptions
    {
        /// <summary>
        /// Minimum time message will stay in cache before it is available for time based purge.
        /// </summary>
        public TimeSpan DataMinTimeInCache { get; set; } = DefaultDataMinTimeInCache;
        /// <summary>
        /// Default DataMinTimeInCache
        /// </summary>
        public static readonly TimeSpan DefaultDataMinTimeInCache = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Difference in time between the newest and oldest messages in the cache.  Any messages older than this will be purged from the cache.
        /// </summary>
        public TimeSpan DataMaxAgeInCache { get; set; } = DefaultDataMaxAgeInCache;
        /// <summary>
        /// Default DataMaxAgeInCache
        /// </summary>
        public static readonly TimeSpan DefaultDataMaxAgeInCache = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Minimum time message metadata (<see cref="StreamSequenceToken"/>) will stay in cache before it is available for time based purge.
        /// Used to avoid cache miss if the full message was purged.
        /// Set to null to disable this tracking.
        /// </summary>
        public TimeSpan? MetadataMinTimeInCache { get; set; } = DefaultMetadataMinTimeInCache;

        /// <summary>
        /// Default MetadataMinTimeInCache
        /// </summary>
        public static readonly TimeSpan DefaultMetadataMinTimeInCache = DefaultDataMinTimeInCache.Multiply(2);
    }

    public class StreamStatisticOptions
    {
        /// <summary>
        /// Statistic monitor write interval
        /// Statistics generation is triggered by activity.  Interval will be ignored when streams are inactive.
        /// </summary>
        public TimeSpan StatisticMonitorWriteInterval { get; set; } = DefaultStatisticMonitorWriteInterval;
        /// <summary>
        /// Default statistic monitor write interval
        /// </summary>
        public static TimeSpan DefaultStatisticMonitorWriteInterval = TimeSpan.FromMinutes(5);
    }
}
