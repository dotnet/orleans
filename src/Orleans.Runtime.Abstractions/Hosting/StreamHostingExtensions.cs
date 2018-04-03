using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Hosting;
using Orleans.Streams;
using Orleans.Providers.Streams.Common;
using Orleans.Providers;
using Orleans.Configuration;
using Orleans.Providers.Streams.SimpleMessageStream;

namespace Orleans.Hosting
{
    public static class StreamHostingExtensions
    {
        /// <summary>
        /// Configure silo to use persistent streams.
        /// </summary>
        public static ISiloHostBuilder AddPersistentStreams(this ISiloHostBuilder builder, string name,
            Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory,
            Action<ISiloPersistentStreamConfigurator> configureStream)
        {
            //the constructor wire up DI with all default components of the streams , so need to be called regardless of configureStream null or not
            var streamConfigurator = new SiloPersistentStreamConfigurator(name, builder, adapterFactory);
            configureStream?.Invoke(streamConfigurator);
            return builder;
        }

        /// <summary>
        /// Configure silo to use SimpleMessageProvider
        /// </summary>
        public static ISiloHostBuilder AddSimpleMessageStreamProvider(this ISiloHostBuilder builder, string name,
            Action<SimpleMessageStreamProviderOptions> configureOptions)

        {
            return builder.ConfigureServices(services =>
                services.AddSiloSimpleMessageStreamProvider(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use SimpleMessageProvider
        /// </summary>
        public static ISiloHostBuilder AddSimpleMessageStreamProvider(this ISiloHostBuilder builder, string name,
            Action<OptionsBuilder<SimpleMessageStreamProviderOptions>> configureOptions = null)

        {
            return builder.ConfigureServices(services =>
                services.AddSiloSimpleMessageStreamProvider(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use simple message stream provider
        /// </summary>
        public static IServiceCollection AddSiloSimpleMessageStreamProvider(this IServiceCollection services, string name,
            Action<SimpleMessageStreamProviderOptions> configureOptions = null)
        {
            return services.AddSiloSimpleMessageStreamProvider(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use simple message provider
        /// </summary>
        public static IServiceCollection AddSiloSimpleMessageStreamProvider(this IServiceCollection services, string name,
        Action<OptionsBuilder<SimpleMessageStreamProviderOptions>> configureOptions = null)
        {
            configureOptions?.Invoke(services.AddOptions<SimpleMessageStreamProviderOptions>(name));
            return services.ConfigureNamedOptionForLogging<SimpleMessageStreamProviderOptions>(name)
                           .AddSingletonNamedService<IStreamProvider>(name, SimpleMessageStreamProvider.Create);
        }
    }
}
