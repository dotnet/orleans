
using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.ServiceBus.Providers;

namespace Orleans.Hosting
{
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Configure cluster client to use event hub persistent streams.
        /// </summary>
        public static IClientBuilder AddEventHubStreams(this IClientBuilder builder, string name, Action<EventHubStreamOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddClusterClientEventHubStreams(name, configureOptions));
        }

        /// <summary>
        /// Configure cluster client to use event hub persistent streams.
        /// </summary>
        public static IClientBuilder AddEventHubStreams(this IClientBuilder builder, string name, Action<OptionsBuilder<EventHubStreamOptions>> configureOptions = null)
        {
            return builder.ConfigureServices(services => services.AddClusterClientEventHubStreams(name, configureOptions));
        }

        /// <summary>
        /// Configure cluster client to use event hub persistent streams.
        /// </summary>
        public static IServiceCollection AddClusterClientEventHubStreams(this IServiceCollection services, string name, Action<EventHubStreamOptions> configureOptions)
        {
            return services.AddClusterClientEventHubStreams(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure cluster client to use event hub persistent streams.
        /// </summary>
        public static IServiceCollection AddClusterClientEventHubStreams(this IServiceCollection services, string name,
            Action<OptionsBuilder<EventHubStreamOptions>> configureOptions = null)
        {
            return services.ConfigureNamedOptionForLogging<EventHubStreamOptions>(name)
                           .AddClusterClientPersistentStreams<EventHubStreamOptions>(name, EventHubAdapterFactory.Create, configureOptions);
        }
    }
}
