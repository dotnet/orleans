using System;
using Orleans.Configuration;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;

namespace Orleans.Hosting
{
    public static class ClientBuilderExtensions
    {

        /// <summary>
        /// Configure cluster client to use event hub persistent streams.
        /// </summary>
        public static IClientBuilder AddEventHubStreams(
           this IClientBuilder builder,
           string name,
           Action<ClusterClientEventHubStreamConfigurator> configure)
        {
            var configurator = new ClusterClientEventHubStreamConfigurator(name,builder);
            configure?.Invoke(configurator);
            return builder;
        }

        /// <summary>
        /// Configure cluster client to use event hub persistent streams with default settings.
        /// </summary>
        public static IClientBuilder AddEventHubStreams(
            this IClientBuilder builder,
            string name, Action<EventHubOptions> configureEventHub)
        {
            builder.AddEventHubStreams(name, b=>b.ConfigureEventHub(ob => ob.Configure(configureEventHub)));
            return builder;
        }
    }
}