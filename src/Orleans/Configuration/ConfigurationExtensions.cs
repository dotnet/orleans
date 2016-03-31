using System;
using System.Collections.Generic;
using Orleans.Providers.Streams.SimpleMessageStream;
using Orleans.Streams;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Extension methods for configuration classes specific to Orleans.dll 
    /// </summary>
    public static class ConfigurationExtensions
    {
        /// <summary>
        /// Adds a stream provider of type <see cref="SimpleMessageStreamProvider"/>
        /// </summary>
        /// <param name="config">The cluster configuration object to add provider to.</param>
        /// <param name="providerName">The provider name.</param>
        /// <param name="fireAndForgetDelivery">Specifies whether the producer waits for the consumer to process the event before continuing. Setting this to false is useful for troubleshooting serialization issues.</param>
        /// <param name="pubSubType">Specifies how can grains subscribe to this stream.</param>
        public static void AddSimpleMessageStreamProvider(this ClusterConfiguration config, string providerName, bool fireAndForgetDelivery = false, StreamPubSubType pubSubType = SimpleMessageStreamProvider.DEFAULT_STREAM_PUBSUB_TYPE)
        {
            var properties = GetSimpleMessageStreamProviderConfiguration(providerName, fireAndForgetDelivery, pubSubType);
            config.Globals.RegisterStreamProvider<SimpleMessageStreamProvider>(providerName, properties);
        }

        /// <summary>
        /// Adds a stream provider of type <see cref="SimpleMessageStreamProvider"/>
        /// </summary>
        /// <param name="config">The cluster configuration object to add provider to.</param>
        /// <param name="providerName">The provider name.</param>
        /// <param name="fireAndForgetDelivery">Specifies whether the producer waits for the consumer to process the event before continuing. Setting this to false is useful for troubleshooting serialization issues.</param>
        /// <param name="pubSubType">Specifies how can grains subscribe to this stream.</param>
        public static void AddSimpleMessageStreamProvider(this ClientConfiguration config, string providerName, bool fireAndForgetDelivery = false, StreamPubSubType pubSubType = SimpleMessageStreamProvider.DEFAULT_STREAM_PUBSUB_TYPE)
        {
            var properties = GetSimpleMessageStreamProviderConfiguration(providerName, fireAndForgetDelivery, pubSubType);
            config.RegisterStreamProvider<SimpleMessageStreamProvider>(providerName, properties);
        }

        private static Dictionary<string, string> GetSimpleMessageStreamProviderConfiguration(string providerName, bool fireAndForgetDelivery, StreamPubSubType pubSubType)
        {
            if (string.IsNullOrWhiteSpace(providerName)) throw new ArgumentNullException(nameof(providerName));

            var properties = new Dictionary<string, string>
            {
                { SimpleMessageStreamProvider.FIRE_AND_FORGET_DELIVERY, fireAndForgetDelivery.ToString() },
                { SimpleMessageStreamProvider.STREAM_PUBSUB_TYPE, pubSubType.ToString() },
            };

            return properties;
        }
    }
}