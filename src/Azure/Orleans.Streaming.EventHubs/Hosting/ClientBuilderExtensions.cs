using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
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
            return builder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(EventHubAdapterFactory).Assembly))
                .ConfigureServices(services => services.AddClusterClientEventHubStreams(name, configureOptions));
        }

        /// <summary>
        /// Configure cluster client to use event hub persistent streams.
        /// </summary>
        public static IClientBuilder AddEventHubStreams(this IClientBuilder builder, string name, Action<OptionsBuilder<EventHubStreamOptions>> configureOptions = null)
        {
            return builder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(EventHubAdapterFactory).Assembly))
                .ConfigureServices(services => services.AddClusterClientEventHubStreams(name, configureOptions));
        }

        /// <summary>
        /// Configure cluster client to use event hub persistent streams.
        /// </summary>
        private static void AddClusterClientEventHubStreams(this IServiceCollection services, string name, Action<EventHubStreamOptions> configureOptions)
        {
            services.AddClusterClientEventHubStreams(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure cluster client to use event hub persistent streams.
        /// </summary>
        private static void AddClusterClientEventHubStreams(this IServiceCollection services, string name,
            Action<OptionsBuilder<EventHubStreamOptions>> configureOptions = null)
        {
            services.ConfigureNamedOptionForLogging<EventHubStreamOptions>(name)
                           .AddClusterClientPersistentStreams<EventHubStreamOptions>(name, EventHubAdapterFactory.Create, configureOptions);
        }
    }
}
