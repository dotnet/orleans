using System;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Streams;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use memory streams. This return a configurator which allows further configuration.
        /// </summary>
        public static SiloMemoryStreamConfigurator<TSerializer> AddMemoryStreams<TSerializer>(this ISiloHostBuilder builder, string name)
             where TSerializer : class, IMemoryMessageBodySerializer
        {
            return new SiloMemoryStreamConfigurator<TSerializer>(name, builder);
        }

        /// <summary>
        /// Configure silo to use memory streams.
        /// </summary>
        public static ISiloHostBuilder AddMemoryStreams<TSerializer>(this ISiloHostBuilder builder, string name,
            Action<SiloMemoryStreamConfigurator<TSerializer>> configure)
             where TSerializer : class, IMemoryMessageBodySerializer
        {
            configure?.Invoke(builder.AddMemoryStreams<TSerializer>(name));
            return builder;
        }
    }
}
