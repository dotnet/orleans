using System;
using Orleans.Configuration;
using Orleans.Providers.GCP.Streams.PubSub;
using Orleans.Streams;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use PubSub persistent streams.
        /// </summary>
        public static SiloPubSubStreamConfigurator<TDataAdapter> ConfigurePubSubStreams<TDataAdapter>(
            this ISiloHostBuilder builder,
            string name)
            where TDataAdapter : IPubSubDataAdapter
        {
            return new SiloPubSubStreamConfigurator<TDataAdapter>(name, builder);
        }

        /// <summary>
        /// Configure silo to use PubSub persistent streams.
        /// </summary>
        public static ISiloHostBuilder AddPubSubStreams<TDataAdapter>(
            this ISiloHostBuilder builder,
            string name, Action<PubSubOptions> configurePubSub)
            where TDataAdapter : IPubSubDataAdapter
        {
            builder.ConfigurePubSubStreams<TDataAdapter>(name)
                .ConfigurePubSub(ob => ob.Configure(configurePubSub));
            return builder;
        }
    }
}