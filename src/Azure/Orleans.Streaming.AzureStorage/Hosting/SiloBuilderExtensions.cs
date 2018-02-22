using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers.Streams.AzureQueue;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use azure queue persistent streams.
        /// </summary>
        public static ISiloHostBuilder AddAzureQueueStreams<TDataAdapter>(this ISiloHostBuilder builder, string name, Action<AzureQueueStreamOptions> configureOptions)
           where TDataAdapter : IAzureQueueDataAdapter
        {
            return builder.ConfigureServices(services => services.AddSiloAzureQueueStreams<TDataAdapter>(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use azure queue persistent streams.
        /// </summary>
        public static ISiloHostBuilder AddAzureQueueStreams<TDataAdapter>(this ISiloHostBuilder builder, string name, Action<OptionsBuilder<AzureQueueStreamOptions>> configureOptions = null)
           where TDataAdapter : IAzureQueueDataAdapter
        {
            return builder.ConfigureServices(services => services.AddSiloAzureQueueStreams<TDataAdapter>(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use azure queue persistent streams.
        /// </summary>
        public static IServiceCollection AddSiloAzureQueueStreams<TDataAdapter>(this IServiceCollection services, string name, Action<AzureQueueStreamOptions> configureOptions)
           where TDataAdapter : IAzureQueueDataAdapter
        {
            return services.AddSiloAzureQueueStreams<TDataAdapter>(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use azure queue persistent streams.
        /// </summary>
        public static IServiceCollection AddSiloAzureQueueStreams<TDataAdapter>(this IServiceCollection services, string name,
            Action<OptionsBuilder<AzureQueueStreamOptions>> configureOptions = null)
           where TDataAdapter : IAzureQueueDataAdapter
        {
            return services.ConfigureNamedOptionForLogging<AzureQueueStreamOptions>(name)
                           .AddSiloPersistentStreams<AzureQueueStreamOptions>(name, AzureQueueAdapterFactory<TDataAdapter>.Create, configureOptions);
        }
    }
}
