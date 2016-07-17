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
        /// <param name="optimizeForImmutableData">If set to true items transfered via the stream are always wrapped in Immutable for delivery.</param>
        /// <param name="pubSubType">Specifies how can grains subscribe to this stream.</param>
        public static void AddSimpleMessageStreamProvider(this ClusterConfiguration config, string providerName, 
            bool fireAndForgetDelivery = SimpleMessageStreamProvider.DEFAULT_VALUE_FIRE_AND_FORGET_DELIVERY, 
            bool optimizeForImmutableData = SimpleMessageStreamProvider.DEFAULT_VALUE_OPTIMIZE_FOR_IMMUTABLE_DATA,
            StreamPubSubType pubSubType = SimpleMessageStreamProvider.DEFAULT_STREAM_PUBSUB_TYPE)
        {
            var properties = GetSimpleMessageStreamProviderConfiguration(providerName, fireAndForgetDelivery, optimizeForImmutableData, pubSubType);
            config.Globals.RegisterStreamProvider<SimpleMessageStreamProvider>(providerName, properties);
        }

        /// <summary>
        /// Adds a stream provider of type <see cref="SimpleMessageStreamProvider"/>
        /// </summary>
        /// <param name="config">The cluster configuration object to add provider to.</param>
        /// <param name="providerName">The provider name.</param>
        /// <param name="fireAndForgetDelivery">Specifies whether the producer waits for the consumer to process the event before continuing. Setting this to false is useful for troubleshooting serialization issues.</param>
        /// <param name="optimizeForImmutableData">If set to true items transfered via the stream are always wrapped in Immutable for delivery.</param>>
        /// <param name="pubSubType">Specifies how can grains subscribe to this stream.</param>
        public static void AddSimpleMessageStreamProvider(this ClientConfiguration config, string providerName,
            bool fireAndForgetDelivery = SimpleMessageStreamProvider.DEFAULT_VALUE_FIRE_AND_FORGET_DELIVERY,
            bool optimizeForImmutableData = SimpleMessageStreamProvider.DEFAULT_VALUE_OPTIMIZE_FOR_IMMUTABLE_DATA,
            StreamPubSubType pubSubType = SimpleMessageStreamProvider.DEFAULT_STREAM_PUBSUB_TYPE)
        {
            var properties = GetSimpleMessageStreamProviderConfiguration(providerName, fireAndForgetDelivery, optimizeForImmutableData, pubSubType);
            config.RegisterStreamProvider<SimpleMessageStreamProvider>(providerName, properties);
        }

        private static Dictionary<string, string> GetSimpleMessageStreamProviderConfiguration(string providerName, bool fireAndForgetDelivery, bool optimizeForImmutableData, StreamPubSubType pubSubType)
        {
            if (string.IsNullOrWhiteSpace(providerName)) throw new ArgumentNullException(nameof(providerName));

            var properties = new Dictionary<string, string>
            {
                { SimpleMessageStreamProvider.FIRE_AND_FORGET_DELIVERY, fireAndForgetDelivery.ToString() },
                { SimpleMessageStreamProvider.OPTIMIZE_FOR_IMMUTABLE_DATA, optimizeForImmutableData.ToString() },
                { SimpleMessageStreamProvider.STREAM_PUBSUB_TYPE, pubSubType.ToString() },
            };

            return properties;
        }


        /// <summary>
        /// Configures all cluster nodes to use the specified startup class for dependency injection.
        /// </summary>
        /// <typeparam name="TStartup">Startup type</typeparam>
        public static void UseStartupType<TStartup>(this ClusterConfiguration config) 
        {
            var startupName = typeof(TStartup).AssemblyQualifiedName;

            foreach(var nodeConfig in config.Overrides.Values) {
                nodeConfig.StartupTypeName = startupName;
            }

            config.Defaults.StartupTypeName = startupName;
        }

    }
}