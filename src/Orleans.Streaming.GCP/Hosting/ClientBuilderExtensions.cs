using System;
using Orleans.Configuration;
using Orleans.Providers.GCP.Streams.PubSub;
using Orleans.Streams;

namespace Orleans.Hosting
{
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Configure cluster client to use PubSub persistent streams.
        /// </summary>
        public static ClusterClientPubSubStreamConfigurator<TDataAdapter> AddPubSubStreams<TDataAdapter>(
            this IClientBuilder builder,
            string name)
            where TDataAdapter : IPubSubDataAdapter
        {
            return new ClusterClientPubSubStreamConfigurator<TDataAdapter>(name, builder);
        }
    }
}
