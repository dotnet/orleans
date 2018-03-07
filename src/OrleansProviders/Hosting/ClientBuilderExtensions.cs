using System;
using Orleans.Configuration;
using Orleans.Providers;

namespace Orleans.Hosting
{
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Configure cluster client to use memory streams.
        /// </summary>
        public static ClusterClientMemoryStreamConfigurator<TSerializer> AddMemoryStreams<TSerializer>(
            this IClientBuilder builder,
            string name)
            where TSerializer : class, IMemoryMessageBodySerializer
        {
            return new ClusterClientMemoryStreamConfigurator<TSerializer>(name, builder);
        }
    }
}