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
        public static ISiloHostBuilder AddPubSubStreams<TDataAdapter>(
            this ISiloHostBuilder builder,
            string name, Action<PubSubOptions> configurePubSub)
            where TDataAdapter : IPubSubDataAdapter
        {
            builder.AddPubSubStreams<TDataAdapter>(name, b=>
                b.ConfigurePubSub(ob => ob.Configure(configurePubSub)));
            return builder;
        }

        /// <summary>
        /// Configure silo to use PubSub persistent streams.
        /// </summary>
        public static ISiloHostBuilder AddPubSubStreams<TDataAdapter>(
            this ISiloHostBuilder builder,
            string name, Action<SiloPubSubStreamConfigurator<TDataAdapter>> configure)
            where TDataAdapter : IPubSubDataAdapter
        {
            var configurator = new SiloPubSubStreamConfigurator<TDataAdapter>(name,
                configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate),
                configureAppPartsDelegate => builder.ConfigureApplicationParts(configureAppPartsDelegate));
            configure?.Invoke(configurator);
            return builder;
        }

        /// <summary>
        /// Configure silo to use PubSub persistent streams.
        /// </summary>
        public static ISiloBuilder AddPubSubStreams<TDataAdapter>(
            this ISiloBuilder builder,
            string name, Action<PubSubOptions> configurePubSub)
            where TDataAdapter : IPubSubDataAdapter
        {
            builder.AddPubSubStreams<TDataAdapter>(name, b=>
                b.ConfigurePubSub(ob => ob.Configure(configurePubSub)));
            return builder;
        }

        /// <summary>
        /// Configure silo to use PubSub persistent streams.
        /// </summary>
        public static ISiloBuilder AddPubSubStreams<TDataAdapter>(
            this ISiloBuilder builder,
            string name, Action<SiloPubSubStreamConfigurator<TDataAdapter>> configure)
            where TDataAdapter : IPubSubDataAdapter
        {
            var configurator = new SiloPubSubStreamConfigurator<TDataAdapter>(name,
                configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate),
                configureAppPartsDelegate => builder.ConfigureApplicationParts(configureAppPartsDelegate));
            configure?.Invoke(configurator);
            return builder;
        }
    }
}