using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Common stream provider settings shared by EventHubStreamProvider, MemoryStreamProvider and GeneratorStreamProvider
    /// </summary>
    public class RecoverableStreamProviderSettings
    {
        /// <summary>
        /// DataMinTimeInCache setting name.
        /// </summary>
        public const string DataMinTimeInCacheName = "DataMinTimeInCache";
        /// <summary>
        /// Drfault DataMinTimeInCache
        /// </summary>
        public static readonly TimeSpan DefaultDataMinTimeInCache = TimeSpan.FromMinutes(5);
        /// <summary>
        /// Minimum time message will stay in cache before it is available for time based purge.
        /// </summary>
        public TimeSpan DataMinTimeInCache { get; set; } = DefaultDataMinTimeInCache;

        /// <summary>
        /// DataMaxAgeInCache setting name.
        /// </summary>
        public const string DataMaxAgeInCacheName = "DataMaxAgeInCache";
        /// <summary>
        /// Default DataMaxAgeInCache
        /// </summary>
        public static readonly TimeSpan DefaultDataMaxAgeInCache = TimeSpan.FromMinutes(30);
        /// <summary>
        /// Difference in time between the newest and oldest messages in the cache.  Any messages older than this will be purged from the cache.
        /// </summary>
        public TimeSpan DataMaxAgeInCache { get; set; } = DefaultDataMaxAgeInCache;

        /// <summary>
        /// Name of StatisticMonitorWriteInterval
        /// </summary>
        public const string StatisticMonitorWriteIntervalName = nameof(StatisticMonitorWriteInterval);

        /// <summary>
        /// Default statistic monitor write interval
        /// </summary>
        public static TimeSpan DefaultStatisticMonitorWriteInterval = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Statistic monitor write interval
        /// </summary>
        public TimeSpan StatisticMonitorWriteInterval { get; set; } = DefaultStatisticMonitorWriteInterval;

        public virtual void WriteProperties(Dictionary<string, string> properties)
        {
            properties.Add(StatisticMonitorWriteIntervalName, StatisticMonitorWriteInterval.ToString());
            properties.Add(DataMinTimeInCacheName, DataMinTimeInCache.ToString());
            properties.Add(DataMaxAgeInCacheName, DataMaxAgeInCache.ToString());
        }

        /// <summary>
        /// Read settings from provider configuration.
        /// </summary>
        /// <param name="providerConfiguration"></param>
        public virtual void PopulateFromProviderConfig(IProviderConfiguration providerConfiguration)
        {
            DataMinTimeInCache = providerConfiguration.GetTimeSpanProperty(DataMinTimeInCacheName, DefaultDataMinTimeInCache);
            DataMaxAgeInCache = providerConfiguration.GetTimeSpanProperty(DataMaxAgeInCacheName, DefaultDataMaxAgeInCache);
            StatisticMonitorWriteInterval = providerConfiguration.GetTimeSpanProperty(StatisticMonitorWriteIntervalName,
                DefaultStatisticMonitorWriteInterval);
        }
    }
}
