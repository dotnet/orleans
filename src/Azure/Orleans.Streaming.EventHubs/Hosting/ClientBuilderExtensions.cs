using System;
using Orleans.Configuration;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;

namespace Orleans.Hosting
{
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Configure cluster client to use event hub persistent streams.
        /// </summary>
        public static ClusterClientEventHubStreamConfigurator AddEventHubStreams(
            this IClientBuilder builder,
            string name)
        {
            return new ClusterClientEventHubStreamConfigurator(name, builder);
        }
    }
}