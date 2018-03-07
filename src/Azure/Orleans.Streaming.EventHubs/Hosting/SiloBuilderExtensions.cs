using System;
using Orleans.Configuration;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use event hub persistent streams.
        /// </summary>
        public static SiloEventHubStreamConfigurator AddEventHubStreams(
            this ISiloHostBuilder builder,
            string name)
        {
            return new SiloEventHubStreamConfigurator(name, builder);
        }
    }
}