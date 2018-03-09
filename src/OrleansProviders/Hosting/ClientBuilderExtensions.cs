using System;
using Orleans.Configuration;
using Orleans.Providers;

namespace Orleans.Hosting
{
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Configure cluster client to use memory streams. This return a configurator for further configuration
        /// </summary>
        public static ClusterClientMemoryStreamConfigurator<TSerializer> ConfigureMemoryStreams<TSerializer>(
            this IClientBuilder builder,
            string name)
            where TSerializer : class, IMemoryMessageBodySerializer
        {
            return new ClusterClientMemoryStreamConfigurator<TSerializer>(name, builder);
        }

        /// <summary>
        /// Configure cluster client to use memory streams with default settings.
        /// </summary>
        public static IClientBuilder AddMemoryStreams<TSerializer>(
            this IClientBuilder builder,
            string name)
            where TSerializer : class, IMemoryMessageBodySerializer
        {
            builder.ConfigureMemoryStreams<TSerializer>(name);
            return builder;
        }
    }
}