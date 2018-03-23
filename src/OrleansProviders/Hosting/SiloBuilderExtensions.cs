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
        public static ISiloHostBuilder AddMemoryStreams<TSerializer>(this ISiloHostBuilder builder, string name,
            Action<SiloMemoryStreamConfigurator<TSerializer>> configure = null)
             where TSerializer : class, IMemoryMessageBodySerializer
        {
            //the constructor wire up DI with all default components of the streams , so need to be called regardless of configureStream null or not
            var memoryStreamConfiguretor = new SiloMemoryStreamConfigurator<TSerializer>(name, builder);
            configure?.Invoke(memoryStreamConfiguretor);
            return builder;
        }
    }
}
