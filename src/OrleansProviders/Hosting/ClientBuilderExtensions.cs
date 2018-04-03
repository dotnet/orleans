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
        public static IClientBuilder AddMemoryStreams<TSerializer>(
            this IClientBuilder builder,
            string name,
            Action<ClusterClientMemoryStreamConfigurator<TSerializer>> configure = null)
            where TSerializer : class, IMemoryMessageBodySerializer
        {
            //the constructor wire up DI with all default components of the streams , so need to be called regardless of configureStream null or not
            var memoryStreamConfigurator = new ClusterClientMemoryStreamConfigurator<TSerializer>(name, builder);
            configure?.Invoke(memoryStreamConfigurator);
            return builder;
        }
    }
}