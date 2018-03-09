using System;
using Orleans.Configuration;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;

namespace Orleans.Hosting
{
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Configure cluster client to use event hub persistent streams. This return a configurator which allows further configuration
        /// </summary>
        public static ClusterClientEventHubStreamConfigurator ConfigureEventHubStreams(
            this IClientBuilder builder,
            string name)
        {
            return new ClusterClientEventHubStreamConfigurator(name, builder);
        }

        /// <summary>
        /// Configure cluster client to use event hub persistent streams with default settings.
        /// </summary>
        public static IClientBuilder AddEventHubStreams(
            this IClientBuilder builder,
            string name, Action<EventHubOptions> configureEventHub)
        {
            builder.ConfigureEventHubStreams(name).ConfigureEventHub(ob => ob.Configure(configureEventHub));
            return builder;
        }
    }
}