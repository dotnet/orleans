using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Hosting
{
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Configure cluster client to use azure queue persistent streams.
        /// </summary>
        public static IClientBuilder AddAzureQueueStreams(this IClientBuilder builder,
            string name,
            Action<ClusterClientAzureQueueStreamConfigurator> configure)
        {
            //the constructor wires up DI with AzureQueueStream, so has to be called regardless configure is null or not
            var configurator = new ClusterClientAzureQueueStreamConfigurator(name, builder);
            configure?.Invoke(configurator);
            return builder;
        }

        /// <summary>
        /// Configure cluster client to use azure queue persistent streams.
        /// </summary>
        public static IClientBuilder AddAzureQueueStreams(this IClientBuilder builder,
            string name, Action<OptionsBuilder<AzureQueueOptions>> configureOptions)
        {
            builder.AddAzureQueueStreams(name, b=>
                 b.ConfigureAzureQueue(configureOptions));
            return builder;
        }
    }
}
