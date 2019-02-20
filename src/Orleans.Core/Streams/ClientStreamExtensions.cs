
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Providers.Streams.SimpleMessageStream;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Hosting
{
    /// <summary>
    /// Configure silo to use persistent streams.
    /// </summary>
    public static class ClientStreamExtensions
    {
        public static IClientBuilder AddPersistentStreams(
            this IClientBuilder builder,
            string name,
            Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory,
            Action<IClusterClientPersistentStreamConfigurator> configureStream)
        {
            //the constructor wire up DI with all default components of the streams , so need to be called regardless of configureStream null or not
            var streamConfigurator = new ClusterClientPersistentStreamConfigurator(name, builder, adapterFactory);
            configureStream?.Invoke(streamConfigurator);
            return builder;
        }

        /// <summary>
        /// Configure client to use SimpleMessageProvider
        /// </summary>
        public static IClientBuilder AddSimpleMessageStreamProvider(
            this IClientBuilder builder,
            string name,
            Action<SimpleMessageStreamProviderOptions> configureOptions)

        {
            return builder.ConfigureServices(services =>
                services.AddClusterClientSimpleMessageStreamProvider(name, configureOptions));
        }

        /// <summary>
        /// Configure client to use SimpleMessageProvider
        /// </summary>
        public static IClientBuilder AddSimpleMessageStreamProvider(
            this IClientBuilder builder,
            string name,
            Action<OptionsBuilder<SimpleMessageStreamProviderOptions>> configureOptions = null)

        {
            return builder.ConfigureServices(services =>
                services.AddClusterClientSimpleMessageStreamProvider(name, configureOptions));
        }

        /// <summary>
        /// Configure client to use simple message stream provider
        /// </summary>
        private static void AddClusterClientSimpleMessageStreamProvider(
            this IServiceCollection services,
            string name,
            Action<SimpleMessageStreamProviderOptions> configureOptions = null)
        {
            services.AddClusterClientSimpleMessageStreamProvider(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure client to use simple message provider
        /// </summary>
        private static void AddClusterClientSimpleMessageStreamProvider(
            this IServiceCollection services,
            string name,
            Action<OptionsBuilder<SimpleMessageStreamProviderOptions>> configureOptions = null)
        {
            configureOptions?.Invoke(services.AddOptions<SimpleMessageStreamProviderOptions>(name));
            services.ConfigureNamedOptionForLogging<SimpleMessageStreamProviderOptions>(name)
                .AddSingletonNamedService<IStreamProvider>(name, SimpleMessageStreamProvider.Create);
        }
    }
}
