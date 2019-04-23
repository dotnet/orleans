using System;
using Orleans.Configuration;
using Orleans.Streams;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {

        /// <summary>
        /// Configure silo to use event hub persistent streams.
        /// </summary>
        public static ISiloHostBuilder AddEventHubStreams(
            this ISiloHostBuilder builder,
            string name,
            Action<ISiloEventHubStreamConfigurator> configure)
        {
            var configurator = new SiloEventHubStreamConfigurator(name,
                configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate),
                configureAppPartsDelegate => builder.ConfigureApplicationParts(configureAppPartsDelegate));
            configure?.Invoke(configurator);
            return builder;
        }

        /// <summary>
        /// Configure silo to use event hub persistent streams with default check pointer and other settings
        /// </summary>
        public static ISiloHostBuilder AddEventHubStreams(
            this ISiloHostBuilder builder,
            string name, Action<EventHubOptions> configureEventHub, Action<AzureTableStreamCheckpointerOptions> configureDefaultCheckpointer)
        {
            return builder.AddEventHubStreams(name, b =>
            {
                b.ConfigureEventHub(ob => ob.Configure(configureEventHub));
                b.UseAzureTableCheckpointer(ob => ob.Configure(configureDefaultCheckpointer));
            });
        }

        /// <summary>
        /// Configure silo to use event hub persistent streams.
        /// </summary>
        public static ISiloBuilder AddEventHubStreams(
            this ISiloBuilder builder,
            string name,
            Action<ISiloEventHubStreamConfigurator> configure)
        {
            var configurator = new SiloEventHubStreamConfigurator(name,
                configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate),
                configureAppPartsDelegate => builder.ConfigureApplicationParts(configureAppPartsDelegate));
            configure?.Invoke(configurator);
            return builder;
        }

        /// <summary>
        /// Configure silo to use event hub persistent streams with default check pointer and other settings
        /// </summary>
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