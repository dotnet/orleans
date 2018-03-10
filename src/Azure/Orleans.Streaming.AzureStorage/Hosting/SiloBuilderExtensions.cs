using System;
using Orleans.Configuration;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Streaming;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use azure queue persistent streams. This return a configurator which allows further configuration
        /// </summary>
        public static SiloAzureQueueStreamConfigurator<TDataAdapter> AddAzureQueueStreams<TDataAdapter>(this ISiloHostBuilder builder, string name)
           where TDataAdapter : IAzureQueueDataAdapter
        {
            return new SiloAzureQueueStreamConfigurator<TDataAdapter>(name,builder);
        }

        /// <summary>
        /// Configure silo to use azure queue persistent streams. 
        /// </summary>
        public static ISiloHostBuilder AddAzureQueueStreams<TDataAdapter>(this ISiloHostBuilder builder, string name,
            Action<SiloAzureQueueStreamConfigurator<TDataAdapter>> configure)
           where TDataAdapter : IAzureQueueDataAdapter
        {
            configure?.Invoke(builder.AddAzureQueueStreams<TDataAdapter>(name));
            return builder;
        }

        /// <summary>
        /// Configure silo to use azure queue persistent streams with default settings
        /// </summary>
        public static ISiloHostBuilder AddAzureQueueStreams<TDataAdapter>(this ISiloHostBuilder builder, string name, Action<OptionsBuilder<AzureQueueOptions>> configureOptions)
           where TDataAdapter : IAzureQueueDataAdapter
        {
            builder.AddAzureQueueStreams<TDataAdapter>(name)
                 .ConfigureAzureQueue(configureOptions);
            return builder;
        }
    }
}
