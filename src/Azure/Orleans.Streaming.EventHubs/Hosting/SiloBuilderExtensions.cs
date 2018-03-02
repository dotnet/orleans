using System;
using Orleans.Configuration;
using Orleans.ServiceBus.Providers;

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
            Action<EventHubStreamOptions> configureOptions)
        {
            return builder.AddEventHubStreams(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use event hub persistent streams.
        /// </summary>
        public static ISiloHostBuilder AddEventHubStreams(
            this ISiloHostBuilder builder,
            string name,
            Action<OptionsBuilder<EventHubStreamOptions>> configureOptions = null)
        {
            return builder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(EventHubAdapterFactory).Assembly))
                .ConfigureServices(services =>
                {
                    services.ConfigureNamedOptionForLogging<EventHubStreamOptions>(name)
                        .AddSiloPersistentStreams<EventHubStreamOptions>(name, EventHubAdapterFactory.Create, configureOptions);
                });
        }
    }
}