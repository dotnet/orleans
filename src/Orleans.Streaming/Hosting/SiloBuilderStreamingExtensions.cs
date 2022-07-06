using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.Runtime;
using Orleans.Runtime.Providers;
using Orleans.Streams;
using Orleans.Streams.Core;
using Orleans.Streams.Filtering;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extension methods for confiiguring streaming on silos.
    /// </summary>
    public static class SiloBuilderStreamingExtensions
    {
        /// <summary>
        /// Add support for streaming to this application.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddStreaming(this ISiloBuilder builder) => builder.ConfigureServices(services => services.AddSiloStreaming());

        /// <summary>
        /// Configures the silo to use persistent streams.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="name">The provider name.</param>
        /// <param name="adapterFactory">The provider adapter factory.</param>
        /// <param name="configureStream">The stream provider configuration delegate.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddPersistentStreams(
            this ISiloBuilder builder,
            string name,
            Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory,
            Action<ISiloPersistentStreamConfigurator> configureStream)
        {
            //the constructor wire up DI with all default components of the streams , so need to be called regardless of configureStream null or not
            var streamConfigurator = new SiloPersistentStreamConfigurator(name, configureDelegate => builder.ConfigureServices(configureDelegate), adapterFactory);
            configureStream?.Invoke(streamConfigurator);
            return builder;
        }

        /// <summary>
        /// Adds a stream filter. 
        /// </summary>
        /// <typeparam name="T">The stream filter type.</typeparam>
        /// <param name="builder">The builder.</param>
        /// <param name="name">The stream filter name.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddStreamFilter<T>(this ISiloBuilder builder, string name) where T : class, IStreamFilter
        {
            return builder.ConfigureServices(svc => svc.AddStreamFilter<T>(name));
        }

        /// <summary>
        /// Adds a stream filter. 
        /// </summary>
        /// <typeparam name="T">The stream filter type.</typeparam>
        /// <param name="builder">The builder.</param>
        /// <param name="name">The stream filter name.</param>
        /// <returns>The client builder.</returns>
        public static IClientBuilder AddStreamFilter<T>(this IClientBuilder builder, string name) where T : class, IStreamFilter
        {
            return builder.ConfigureServices(svc => svc.AddStreamFilter<T>(name));
        }
    }
}
