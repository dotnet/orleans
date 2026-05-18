using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

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
           Action<IClusterClientEventHubStreamConfigurator> configure)
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

        /// <summary>
        /// Configure cluster client to use event hub persistent streams with default settings.
        /// </summary>
        public static IClientBuilder AddEventHubsStreams(
            this IClientBuilder builder,
            string name,
            Action<OptionsBuilder<EventHubOptions>> configureOptions)
        {

            return builder.AddEventHubStreams(name, b =>
            {
                b.ConfigureEventHub(configureOptions);
            });
        }
    }
}