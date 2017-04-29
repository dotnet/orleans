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
        private TimeSpan? dataMinTimeInCache;
        /// <summary>
        /// Minimum time message will stay in cache before it is available for time based purge.
        /// </summary>
        public TimeSpan DataMinTimeInCache
        {
            get { return dataMinTimeInCache ?? DefaultDataMinTimeInCache; }
            set { dataMinTimeInCache = value; }
        }

        /// <summary>
        /// DataMaxAgeInCache setting name.
        /// </summary>
        public const string DataMaxAgeInCacheName = "DataMaxAgeInCache";
        /// <summary>
        /// Default DataMaxAgeInCache
        /// </summary>
        public static readonly TimeSpan DefaultDataMaxAgeInCache = TimeSpan.FromMinutes(10);
        private TimeSpan? dataMaxAgeInCache;
        /// <summary>
        /// Difference in time between the newest and oldest messages in the cache.  Any messages older than this will be purged from the cache.
        /// </summary>
        public TimeSpan DataMaxAgeInCache
        {
            get { return dataMaxAgeInCache ?? DefaultDataMaxAgeInCache; }
            set { dataMaxAgeInCache = value; }
        }

        /// <summary>
        /// Cache size of FixedSizeObjectPool measured in Mb
        /// </summary>
        public int CacheSizeMb { get; set; } = 10;

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
        }

        /// <summary>
        /// Utility function to populate config from provider config
        /// </summary>
        /// <param name="providerConfiguration"></param>
        public void PopulateFromProviderConfig(IProviderConfiguration providerConfiguration)
        {
            TotalQueueCount = providerConfiguration.GetIntProperty(TotalQueueCountName, TotalQueueCountDefault);
        }
    }
}
