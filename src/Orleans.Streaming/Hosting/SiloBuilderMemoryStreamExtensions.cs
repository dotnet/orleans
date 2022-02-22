using System;
using Orleans.Providers;

namespace Orleans.Hosting
{
    /// <summary>
    /// <see cref="ISiloBuilder"/> extension methods for configuring in-memory streams. 
    /// </summary>
    public static class SiloBuilderMemoryStreamExtensions
    {
        /// <summary>
        /// Configure silo to use memory streams.
        /// </summary>
        /// <typeparam name="TSerializer">The message serializer type, which must implement <see cref="IMemoryMessageBodySerializer"/>.</typeparam>
        /// <param name="builder">The builder.</param>
        /// <param name="name">The stream provider name.</param>
        /// <param name="configure">The configuration delegate.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddMemoryStreams<TSerializer>(this ISiloBuilder builder, string name,
            Action<ISiloMemoryStreamConfigurator> configure = null)
             where TSerializer : class, IMemoryMessageBodySerializer
        {
            //the constructor wire up DI with all default components of the streams , so need to be called regardless of configureStream null or not
            var memoryStreamConfiguretor = new SiloMemoryStreamConfigurator<TSerializer>(name,
                configureDelegate => builder.ConfigureServices(configureDelegate)
            );
            configure?.Invoke(memoryStreamConfiguretor);
            return builder;
        }
    }
}
