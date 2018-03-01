using System;
using Orleans.Configuration;
using Orleans.ServiceBus.Providers;

namespace Orleans.Hosting
{
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Configure cluster client to use event hub persistent streams.
        /// </summary>
        public static IClientBuilder AddEventHubStreams(
            this IClientBuilder builder,
            string name,
            Action<EventHubStreamOptions> configureOptions)
        {
            return builder.AddEventHubStreams(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure cluster client to use event hub persistent streams.
        /// </summary>
        public static IClientBuilder AddEventHubStreams(
            this IClientBuilder builder,
            string name,
            Action<OptionsBuilder<EventHubStreamOptions>> configureOptions = null)
        {
            return builder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(EventHubAdapterFactory).Assembly))
                .ConfigureServices(services =>
                {
                    services.ConfigureNamedOptionForLogging<EventHubStreamOptions>(name)
                        .AddClusterClientPersistentStreams<EventHubStreamOptions>(name, EventHubAdapterFactory.Create, configureOptions);
                });
        }
    }
}