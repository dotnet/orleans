using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Configuration
{
    public class MemoryStreamCacheOptions
    {
        /// <summary>
        /// Minimum time message will stay in cache before it is available for time based purge.
        /// </summary>
        public TimeSpan DataMinTimeInCache { get; set; } = DefaultDataMinTimeInCache;
        /// <summary>
        /// Drfault DataMinTimeInCache
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
    }
}
