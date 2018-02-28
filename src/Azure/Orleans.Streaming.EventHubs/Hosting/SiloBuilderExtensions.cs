using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.ServiceBus.Providers;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use event hub persistent streams.
        /// </summary>
        public static ISiloHostBuilder AddEventHubStreams(this ISiloHostBuilder builder, string name, Action<EventHubStreamOptions> configureOptions)
        {
            return builder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(EventHubAdapterFactory).Assembly))
                .ConfigureServices(services => services.AddSiloEventHubStreams(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use event hub persistent streams.
        /// </summary>
        public static ISiloHostBuilder AddEventHubStreams(this ISiloHostBuilder builder, string name, Action<OptionsBuilder<EventHubStreamOptions>> configureOptions = null)
        {
            return builder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(EventHubAdapterFactory).Assembly))
                .ConfigureServices(services => services.AddSiloEventHubStreams(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use event hub persistent streams.
        /// </summary>
        private static void AddSiloEventHubStreams(this IServiceCollection services, string name, Action<EventHubStreamOptions> configureOptions)
        {
            services.AddSiloEventHubStreams(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use event hub persistent streams.
        /// </summary>
        private static void AddSiloEventHubStreams(this IServiceCollection services, string name,
            Action<OptionsBuilder<EventHubStreamOptions>> configureOptions = null)
        {
            services.ConfigureNamedOptionForLogging<EventHubStreamOptions>(name)
                           .AddSiloPersistentStreams<EventHubStreamOptions>(name, EventHubAdapterFactory.Create, configureOptions);
        }
    }
}
