
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Orleans.Providers.Streams.Generator
{
    /// <summary>
    /// Simple generator configuration class.
    /// This class is used to configure a generator stream provider to generate streams using the SimpleGenerator
    /// </summary>
    [Serializable]
    public class SimpleGeneratorConfig : IStreamGeneratorConfig
    {
        private const string StreamNamespaceName = "StreamNamespace";
        /// <summary>
        /// Stream namespace
        /// </summary>
        public string StreamNamespace { get; set; }

        /// <summary>
        /// Stream generator type
        /// </summary>
        public Type StreamGeneratorType => typeof (SimpleGenerator);

        /// <summary>
        /// Nuber of events to generate on this stream
        /// </summary>
        public int EventsInStream { get; set; }
        private const string EventsInStreamName = "EventsInStream";
        private const int EventsInStreamDefault = 100;

        /// <summary>
        /// Configuration for simple stream generator
        /// </summary>
        public SimpleGeneratorConfig()
        {
            EventsInStream = EventsInStreamDefault;
        }

        /// <summary>
        /// Utility function to convert config to property bag for use in stream provider configuration
        /// </summary>
        /// <returns></returns>
        public void WriteProperties(Dictionary<string, string> properties)
        {
            properties.Add(GeneratorAdapterFactory.GeneratorConfigTypeName, GetType().AssemblyQualifiedName);
            properties.Add(EventsInStreamName, EventsInStream.ToString(CultureInfo.InvariantCulture));
            properties.Add(StreamNamespaceName, StreamNamespace);
        }

        /// <summary>
        /// Utility function to populate config from provider config
        /// </summary>
        /// <param name="providerConfiguration"></param>
        public void PopulateFromProviderConfig(IProviderConfiguration providerConfiguration)
        {
            EventsInStream = providerConfiguration.GetIntProperty(EventsInStreamName, EventsInStreamDefault);
            StreamNamespace = providerConfiguration.GetProperty(StreamNamespaceName, null);
        }
    }
}
