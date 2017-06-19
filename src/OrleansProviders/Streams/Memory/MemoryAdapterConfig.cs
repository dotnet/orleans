using Orleans.Providers.Streams.Common;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Orleans.Providers
{
    /// <summary>
    /// This configuration class is used to configure the MemoryStreamProvider.
    /// It tells the stream provider how many queues to create.
    /// </summary>
    public class MemoryAdapterConfig : RecoverableStreamProviderSettings
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
        public override void WriteProperties(Dictionary<string, string> properties)
        {
            properties.Add(TotalQueueCountName, TotalQueueCount.ToString(CultureInfo.InvariantCulture));
            base.WriteProperties(properties);
        }

        /// <summary>
        /// Utility function to populate config from provider config
        /// </summary>
        /// <param name="providerConfiguration"></param>
        public override void PopulateFromProviderConfig(IProviderConfiguration providerConfiguration)
        {
            TotalQueueCount = providerConfiguration.GetIntProperty(TotalQueueCountName, TotalQueueCountDefault);
            base.PopulateFromProviderConfig(providerConfiguration);
        }
    }
}
