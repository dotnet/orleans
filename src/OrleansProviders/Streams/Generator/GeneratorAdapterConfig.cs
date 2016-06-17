
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Orleans.Providers.Streams.Generator
{
    /// <summary>
    /// This configuration class is used to configure the GeneratorStreamProvider.
    /// It tells the stream provider how many queues to create, and which generator to use to generate event streams.
    /// </summary>
    public class GeneratorAdapterConfig
    {
        /// <summary>
        /// Configuration property name for generator configuration type
        /// </summary>
        public const string GeneratorConfigTypeName = "GeneratorConfigType";

        /// <summary>
        /// Generator configuration type
        /// </summary>
        public Type GeneratorConfigType { get; set; }

        /// <summary>
        /// Stream provider name
        /// </summary>
        public string StreamProviderName { get; }

        private const string TotalQueueCountName = "TotalQueueCount";
        private const int TotalQueueCountDefault = 4;

        /// <summary>
        /// Total number of queues
        /// </summary>
        public int TotalQueueCount { get; set; }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="streamProviderName"></param>
        public GeneratorAdapterConfig(string streamProviderName)
        {
            StreamProviderName = streamProviderName;
            TotalQueueCount = TotalQueueCountDefault;
        }

        /// <summary>
        /// Utility function to convert config to property bag for use in stream provider configuration
        /// </summary>
        /// <returns></returns>
        public void WriteProperties(Dictionary<string, string> properties)
        {
            if (GeneratorConfigType != null)
            {
                properties.Add(GeneratorConfigTypeName, GeneratorConfigType.AssemblyQualifiedName);
            }
            properties.Add(TotalQueueCountName, TotalQueueCount.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Utility function to populate config from provider config
        /// </summary>
        /// <param name="providerConfiguration"></param>
        public virtual void PopulateFromProviderConfig(IProviderConfiguration providerConfiguration)
        {
            GeneratorConfigType = providerConfiguration.GetTypeProperty(GeneratorConfigTypeName, null);
            if (string.IsNullOrWhiteSpace(StreamProviderName))
            {
                throw new ArgumentOutOfRangeException("providerConfiguration", "StreamProviderName not set.");
            }
            TotalQueueCount = providerConfiguration.GetIntProperty(TotalQueueCountName, TotalQueueCountDefault);
        }
    }
}
