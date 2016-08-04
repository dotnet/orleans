using System;
using System.Collections.Generic;
using System.Globalization;

namespace Orleans.Providers.Streams.Memory
{
    /// <summary>
    /// This configuration class is used to configure the MemoryStreamProvider.
    /// It tells the stream provider how many queues to create.
    /// </summary>
    public class MemoryAdapterConfig
    {
        public String StreamProviderName { get; private set; }

        private const string TotalQueueCountName = "TotalQueueCount";
        private const int TotalQueueCountDefault = 4;
        private static int cacheSizeMbDefault = 10;
        public int TotalQueueCount { get; set; }
        public int CacheSizeMb { get; set; } = cacheSizeMbDefault;

        public MemoryAdapterConfig(string streamProviderName, int totalQueueCount = 0)
        {
            if (string.IsNullOrWhiteSpace(streamProviderName))
            {
                throw new ArgumentOutOfRangeException("providerConfiguration", "StreamProviderName not set.");
            }
            this.StreamProviderName = streamProviderName;
            this.TotalQueueCount = totalQueueCount <= 0 ? TotalQueueCountDefault : totalQueueCount;
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
