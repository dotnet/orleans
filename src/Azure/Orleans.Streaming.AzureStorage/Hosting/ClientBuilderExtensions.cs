using System;
using Orleans.Configuration;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Streaming;

namespace Orleans.Hosting
{
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Configure cluster client to use azure queue persistent streams.
        /// </summary>
        public static ClusterClientAzureQueueStreamConfigurator<TDataAdapter> AddAzureQueueStreams<TDataAdapter>(this IClientBuilder builder,
            string name)
            where TDataAdapter : IAzureQueueDataAdapter
        {
            return new ClusterClientAzureQueueStreamConfigurator<TDataAdapter>(name, builder);
        }
    }
}
