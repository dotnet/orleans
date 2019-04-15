using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use azure queue persistent streams.
        /// </summary>
        public static ISiloHostBuilder AddAzureQueueStreams(this ISiloHostBuilder builder, string name,
            Action<SiloAzureQueueStreamConfigurator> configure)
        {
            var configurator = new SiloAzureQueueStreamConfigurator(name,
                configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate),
                configureAppPartsDelegate => builder.ConfigureApplicationParts(configureAppPartsDelegate));
            configure?.Invoke(configurator);
            return builder;
        }

        /// <summary>
        /// Configure silo to use azure queue persistent streams with default settings
        /// </summary>
        public static ISiloHostBuilder AddAzureQueueStreams(this ISiloHostBuilder builder, string name, Action<OptionsBuilder<AzureQueueOptions>> configureOptions)
        {
            builder.AddAzureQueueStreams(name, b =>
                 b.ConfigureAzureQueue(configureOptions));
            return builder;
        }

        /// <summary>
        /// Configure silo to use azure queue persistent streams.
        /// </summary>
        public static ISiloBuilder AddAzureQueueStreams(this ISiloBuilder builder, string name,
            Action<SiloAzureQueueStreamConfigurator> configure)
        {
            var configurator = new SiloAzureQueueStreamConfigurator(name,
                configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate),
                configureAppPartsDelegate => builder.ConfigureApplicationParts(configureAppPartsDelegate));
            configure?.Invoke(configurator);
            return builder;
        }

        /// <summary>
        /// Configure silo to use azure queue persistent streams with default settings
        /// </summary>
        public static ISiloBuilder AddAzureQueueStreams(this ISiloBuilder builder, string name, Action<OptionsBuilder<AzureQueueOptions>> configureOptions)
        {
            builder.AddAzureQueueStreams(name, b =>
                 b.ConfigureAzureQueue(configureOptions));
            return builder;
        }
    }
}
