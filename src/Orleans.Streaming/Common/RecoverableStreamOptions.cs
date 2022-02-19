using System;
using Orleans.Streams;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configuration options for stream cache eviction.
    /// </summary>
    public class StreamCacheEvictionOptions
    {
        /// <summary>
        /// Gets or sets the minimum time a message will stay in cache before it is available for time based purge.
        /// </summary>
        public TimeSpan DataMinTimeInCache { get; set; } = DefaultDataMinTimeInCache;

        /// <summary>
        /// The default value for <see cref="DataMinTimeInCache"/>.
        /// </summary>
        public static readonly TimeSpan DefaultDataMinTimeInCache = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Gets or sets the difference in time between the newest and oldest messages in the cache.  Any messages older than this will be purged from the cache.
        /// </summary>
        public TimeSpan DataMaxAgeInCache { get; set; } = DefaultDataMaxAgeInCache;

        /// <summary>
        /// The default value for <see cref="DataMaxAgeInCache"/>
        /// </summary>
        public static readonly TimeSpan DefaultDataMaxAgeInCache = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Gets or sets the minimum time that message metadata (<see cref="StreamSequenceToken"/>) will stay in cache before it is available for time based purge.
        /// Used to avoid cache miss if the full message was purged.
        /// Set to <see langword="null"/> to disable this tracking.
        /// </summary>
        public TimeSpan? MetadataMinTimeInCache { get; set; } = DefaultMetadataMinTimeInCache;

        /// <summary>
        /// The default value for <see cref="MetadataMinTimeInCache"/>.
        /// </summary>
        public static readonly TimeSpan DefaultMetadataMinTimeInCache = DefaultDataMinTimeInCache.Multiply(2);
    }

    /// <summary>
    /// Configuration options for stream statistics.
    /// </summary>
    public class StreamStatisticOptions
    {
        /// <summary>
        /// Gets or sets the statistic monitor write interval.
        /// Statistics generation is triggered by activity. Interval will be ignored when streams are inactive.
        /// </summary>
        public TimeSpan StatisticMonitorWriteInterval { get; set; } = DefaultStatisticMonitorWriteInterval;

        /// <summary>
        /// The default value for <see cref="StatisticMonitorWriteInterval"/>.
        /// </summary>
        public static readonly TimeSpan DefaultStatisticMonitorWriteInterval = TimeSpan.FromMinutes(5);
    }
}
