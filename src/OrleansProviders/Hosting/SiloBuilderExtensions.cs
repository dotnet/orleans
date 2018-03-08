using System;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Streams;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use memory streams.
        /// </summary>
        public static SiloMemoryStreamConfigurator<TSerializer> AddMemoryStreams<TSerializer>(this ISiloHostBuilder builder, string name)
             where TSerializer : class, IMemoryMessageBodySerializer
        {
            return new SiloMemoryStreamConfigurator<TSerializer>(name, builder);
        }
    }
}
