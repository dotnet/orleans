using System;
using Orleans.Configuration;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Streaming;

namespace Orleans.Hosting
{
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Configure cluster client to use azure queue persistent streams. Returns ClusterClientAzureQueueStreamConfigurator for further configuration
        /// </summary>
        public static ClusterClientAzureQueueStreamConfigurator<TDataAdapter> AddAzureQueueStreams<TDataAdapter>(this IClientBuilder builder,
            string name)
            where TDataAdapter : IAzureQueueDataAdapter
        {
            return new ClusterClientAzureQueueStreamConfigurator<TDataAdapter>(name, builder);
        }

        /// <summary>
        /// Configure cluster client to use azure queue persistent streams. Returns ClusterClientAzureQueueStreamConfigurator for further configuration
        /// </summary>
        public static IClientBuilder AddAzureQueueStreams<TDataAdapter>(this IClientBuilder builder,
            string name,
            Action<ClusterClientAzureQueueStreamConfigurator<TDataAdapter>> configure)
            where TDataAdapter : IAzureQueueDataAdapter
        {
            configure?.Invoke(builder.AddAzureQueueStreams<TDataAdapter>(name));
            return builder;
        }

        /// <summary>
        /// Configure cluster client to use azure queue persistent streams.
        /// </summary>
        public static IClientBuilder AddAzureQueueStreams<TDataAdapter>(this IClientBuilder builder,
            string name, Action<OptionsBuilder<AzureQueueOptions>> configureOptions)
            where TDataAdapter : IAzureQueueDataAdapter
        {
            builder.AddAzureQueueStreams<TDataAdapter>(name)
                 .ConfigureAzureQueue(configureOptions);
            return builder;
        }
    }
}
