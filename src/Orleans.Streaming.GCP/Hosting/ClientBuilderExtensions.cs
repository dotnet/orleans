using System;
using Orleans.Configuration;
using Orleans.Providers.GCP.Streams.PubSub;

namespace Orleans.Hosting
{
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Configure cluster client to use PubSub persistent streams.
        /// </summary>
        public static IClientBuilder AddPubSubStreams<TDataAdapter>(
            this IClientBuilder builder,
            string name, Action<PubSubOptions> configurePubSub)
            where TDataAdapter : IPubSubDataAdapter
        {
            builder.AddPubSubStreams<TDataAdapter>(name, b=>b.ConfigurePubSub(ob => ob.Configure(configurePubSub)));
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
            var configurator = new ClusterClientPubSubStreamConfigurator<TDataAdapter>(name, builder);
            configure?.Invoke(configurator);
            return builder;
        }
    }
}
