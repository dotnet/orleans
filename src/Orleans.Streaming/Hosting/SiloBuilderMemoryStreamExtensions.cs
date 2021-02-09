using System;
using Orleans.Providers;

namespace Orleans.Hosting
{
    public static class SiloBuilderMemoryStreamExtensions
    {
        /// <summary>
        /// Configure silo to use memory streams.
        /// </summary>
        public static ISiloHostBuilder AddMemoryStreams<TSerializer>(this ISiloHostBuilder builder, string name,
            Action<ISiloMemoryStreamConfigurator> configure = null)
             where TSerializer : class, IMemoryMessageBodySerializer
        {
            //the constructor wire up DI with all default components of the streams , so need to be called regardless of configureStream null or not
            var memoryStreamConfiguretor = new SiloMemoryStreamConfigurator<TSerializer>(name,
                configureDelegate => builder.ConfigureServices(configureDelegate),
                configureDelegate => builder.ConfigureApplicationParts(configureDelegate)
            );
            configure?.Invoke(memoryStreamConfiguretor);
            return builder;
        }

        /// <summary>
        /// Configure silo to use memory streams.
        /// </summary>
        public static ISiloBuilder AddMemoryStreams<TSerializer>(this ISiloBuilder builder, string name,
            Action<ISiloMemoryStreamConfigurator> configure = null)
             where TSerializer : class, IMemoryMessageBodySerializer
        {
            //the constructor wire up DI with all default components of the streams , so need to be called regardless of configureStream null or not
            var memoryStreamConfiguretor = new SiloMemoryStreamConfigurator<TSerializer>(name,
                configureDelegate => builder.ConfigureServices(configureDelegate),
                configureDelegate => builder.ConfigureApplicationParts(configureDelegate)
            );
            configure?.Invoke(memoryStreamConfiguretor);
            return builder;
        }
    }
}
