using System;
using Orleans.Configuration;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extension methods for configuring Azure Event Hubs stream providers on an Orleans silo.
    /// </summary>
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Adds a named Azure Event Hubs persistent stream provider to the silo.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="name">The stream provider name.</param>
        /// <param name="configure">The delegate used to configure the stream provider.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddEventHubStreams(
            this ISiloBuilder builder,
            string name,
            Action<ISiloEventHubStreamConfigurator> configure)
        {
            var configurator = new SiloEventHubStreamConfigurator(name,
                configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate));
            configure?.Invoke(configurator);
            return builder;
        }

        /// <summary>
        /// Adds a named Azure Event Hubs persistent stream provider using Azure Table Storage for checkpoints.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="name">The stream provider name.</param>
        /// <param name="configureEventHub">The delegate used to configure the Event Hub connection.</param>
        /// <param name="configureDefaultCheckpointer">The delegate used to configure the Azure Table Storage checkpointer.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddEventHubStreams(
            this ISiloBuilder builder,
            string name, Action<EventHubOptions> configureEventHub, Action<AzureTableStreamCheckpointerOptions> configureDefaultCheckpointer)
        {
            return builder.AddEventHubStreams(name, b =>
            {
                b.ConfigureEventHub(ob => ob.Configure(configureEventHub));
                b.UseAzureTableCheckpointer(ob => ob.Configure(configureDefaultCheckpointer));
            });
        }
    }
}