using System;
using Orleans.Configuration;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extension methods for configuring Azure Event Hubs stream providers on an Orleans client.
    /// </summary>
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Adds a named Azure Event Hubs persistent stream provider to the client.
        /// </summary>
        /// <param name="builder">The client builder.</param>
        /// <param name="name">The stream provider name.</param>
        /// <param name="configure">The delegate used to configure the stream provider.</param>
        /// <returns>The client builder.</returns>
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
        /// Adds a named Azure Event Hubs persistent stream provider to the client.
        /// </summary>
        /// <param name="builder">The client builder.</param>
        /// <param name="name">The stream provider name.</param>
        /// <param name="configureEventHub">The delegate used to configure the Event Hub connection.</param>
        /// <returns>The client builder.</returns>
        public static IClientBuilder AddEventHubStreams(
            this IClientBuilder builder,
            string name, Action<EventHubOptions> configureEventHub)
        {
            builder.AddEventHubStreams(name, b=>b.ConfigureEventHub(ob => ob.Configure(configureEventHub)));
            return builder;
        }
    }
}