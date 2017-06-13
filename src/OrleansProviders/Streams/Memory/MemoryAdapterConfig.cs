using System;
using System.Collections.Generic;
using System.Globalization;

namespace Orleans.Providers
{
    /// <summary>
    /// This configuration class is used to configure the MemoryStreamProvider.
    /// It tells the stream provider how many queues to create.
    /// </summary>
    public class MemoryAdapterConfig
    {
        /// <summary>
        /// Stream provider name.
        /// </summary>
        public String StreamProviderName { get; private set; }

        /// <summary>
        /// Total queue count name. Indicates the name of the property in provider config.
        /// </summary>
        private const string TotalQueueCountName = "TotalQueueCount";

        /// <summary>
        /// Total queue count default value.
        /// </summary>
        private const int TotalQueueCountDefault = 4;

        /// <summary>
        /// Actual total queue count.
        /// </summary>
        public int TotalQueueCount { get; set; }

        //TODO - below time purge configuration is duplicated in eventhub adatper settings - consider adding common time purge configuration - jbragg
        /// <summary>
        /// DataMinTimeInCache setting name.
        /// </summary>
        public const string DataMinTimeInCacheName = "DataMinTimeInCache";
        /// <summary>
        /// Drfault DataMinTimeInCache
        /// </summary>
        public static readonly TimeSpan DefaultDataMinTimeInCache = TimeSpan.FromMinutes(3);
        /// <summary>
        /// Minimum time message will stay in cache before it is available for time based purge.
        /// </summary>
        public TimeSpan DataMinTimeInCache = DefaultDataMinTimeInCache;

        /// <summary>
        /// DataMaxAgeInCache setting name.
        /// </summary>
        public const string DataMaxAgeInCacheName = "DataMaxAgeInCache";
        /// <summary>
        /// Default DataMaxAgeInCache
        /// </summary>
        public static readonly TimeSpan DefaultDataMaxAgeInCache = TimeSpan.FromMinutes(10);
        /// <summary>
        /// Difference in time between the newest and oldest messages in the cache.  Any messages older than this will be purged from the cache.
        /// </summary>
        public TimeSpan DataMaxAgeInCache = DefaultDataMaxAgeInCache;

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
        public TimeSpan StatisticMonitorWriteInterval = DefaultStatisticMonitorWriteInterval;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="streamProviderName"></param>
        /// <param name="totalQueueCount"></param>
        public MemoryAdapterConfig(string streamProviderName, int totalQueueCount = TotalQueueCountDefault)
        {
            if (string.IsNullOrWhiteSpace(streamProviderName))
            {
                throw new ArgumentNullException(nameof(streamProviderName));
            }
            if (totalQueueCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(totalQueueCount), "totalQueueCount must be larger than 0.");
            }
            this.StreamProviderName = streamProviderName;
            this.TotalQueueCount = totalQueueCount;
        }

        /// <summary>
        /// Utility function to convert config to property bag for use in stream provider configuration
        /// </summary>
        /// <returns></returns>
        public void WriteProperties(Dictionary<string, string> properties)
        {
            properties.Add(TotalQueueCountName, TotalQueueCount.ToString(CultureInfo.InvariantCulture));
            properties.Add(DataMinTimeInCacheName, DataMinTimeInCache.ToString());
            properties.Add(DataMaxAgeInCacheName, DataMaxAgeInCache.ToString());
            properties.Add(StatisticMonitorWriteIntervalName, StatisticMonitorWriteInterval.ToString());
        }

        /// <summary>
        /// Utility function to populate config from provider config
        /// </summary>
        /// <param name="providerConfiguration"></param>
        public void PopulateFromProviderConfig(IProviderConfiguration providerConfiguration)
        {
            TotalQueueCount = providerConfiguration.GetIntProperty(TotalQueueCountName, TotalQueueCountDefault);
            DataMinTimeInCache = providerConfiguration.GetTimeSpanProperty(DataMinTimeInCacheName, DefaultDataMinTimeInCache);
            DataMaxAgeInCache = providerConfiguration.GetTimeSpanProperty(DataMaxAgeInCacheName, DefaultDataMaxAgeInCache);
            StatisticMonitorWriteInterval = providerConfiguration.GetTimeSpanProperty(StatisticMonitorWriteIntervalName, DefaultStatisticMonitorWriteInterval);
        }
    }
}
