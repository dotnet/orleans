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


        /// <summary>
        /// Configure cluster client to use PubSub persistent streams.
        /// </summary>
        public static IClientBuilder AddPubSubStreams<TDataAdapter>(
            this IClientBuilder builder,
            string name, Action<PubSubOptions> configurePubSub)
            where TDataAdapter : IPubSubDataAdapter
        {
            builder.AddPubSubStreams<TDataAdapter>(name)
                .ConfigurePubSub(ob => ob.Configure(configurePubSub));
            return builder;
        }

        /// <summary>
        /// Configure cluster client to use PubSub persistent streams.
        /// </summary>
        public static IClientBuilder AddPubSubStreams<TDataAdapter>(
            this IClientBuilder builder,
            string name, Action<ClusterClientPubSubStreamConfigurator<TDataAdapter>> configure)
            where TDataAdapter : IPubSubDataAdapter
        {
            configure?.Invoke(builder.AddPubSubStreams<TDataAdapter>(name));
            return builder;
        }
    }
}
