using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Streaming;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use azure queue persistent streams.
        /// </summary>
        public static ISiloHostBuilder AddAzureQueueStreams<TDataAdapter>(this ISiloHostBuilder builder, string name,
            Action<SiloAzureQueueStreamConfigurator<TDataAdapter>> configure)
           where TDataAdapter : IAzureQueueDataAdapter
        {
            var configurator = new SiloAzureQueueStreamConfigurator<TDataAdapter>(name,
                configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate),
                configureAppPartsDelegate => builder.ConfigureApplicationParts(configureAppPartsDelegate));
            configure?.Invoke(configurator);
            return builder;
        }

        /// <summary>
        /// Configure silo to use azure queue persistent streams with default settings
        /// </summary>
        public static ISiloHostBuilder AddAzureQueueStreams<TDataAdapter>(this ISiloHostBuilder builder, string name, Action<OptionsBuilder<AzureQueueOptions>> configureOptions)
           where TDataAdapter : IAzureQueueDataAdapter
        {
            builder.AddAzureQueueStreams<TDataAdapter>(name, b =>
                 b.ConfigureAzureQueue(configureOptions));
            return builder;
        }

        /// <summary>
        /// Configure silo to use azure queue persistent streams.
        /// </summary>
        public static ISiloBuilder AddAzureQueueStreams<TDataAdapter>(this ISiloBuilder builder, string name,
            Action<SiloAzureQueueStreamConfigurator<TDataAdapter>> configure)
           where TDataAdapter : IAzureQueueDataAdapter
        {
            var configurator = new SiloAzureQueueStreamConfigurator<TDataAdapter>(name,
                configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate),
                configureAppPartsDelegate => builder.ConfigureApplicationParts(configureAppPartsDelegate));
            configure?.Invoke(configurator);
            return builder;
        }

        /// <summary>
        /// Configure silo to use azure queue persistent streams with default settings
        /// </summary>
        public static ISiloBuilder AddAzureQueueStreams<TDataAdapter>(this ISiloBuilder builder, string name, Action<OptionsBuilder<AzureQueueOptions>> configureOptions)
           where TDataAdapter : IAzureQueueDataAdapter
        {
            builder.AddAzureQueueStreams<TDataAdapter>(name, b =>
                 b.ConfigureAzureQueue(configureOptions));
            return builder;
        }
    }
}
