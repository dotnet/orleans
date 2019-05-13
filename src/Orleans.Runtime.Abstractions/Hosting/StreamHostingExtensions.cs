using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Streams;
using Orleans.Configuration;

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
            var streamConfigurator = new SiloPersistentStreamConfigurator(name, configureDelegate => builder.ConfigureServices(configureDelegate), adapterFactory);
            configureStream?.Invoke(streamConfigurator);
            return builder;
        }

        /// <summary>
        /// Configure silo to use SimpleMessageProvider
        /// </summary>
        public static ISiloHostBuilder AddSimpleMessageStreamProvider(
            this ISiloHostBuilder builder,
            string name,
            Action<ISimpleMessageStreamConfigurator> configureStream)
        {
            //the constructor wire up DI with all default components of the streams , so need to be called regardless of configureStream null or not
            var streamConfigurator = new SimpleMessageStreamConfigurator(name, configureDelegate => builder.ConfigureServices(configureDelegate));
            configureStream?.Invoke(streamConfigurator);
            return builder;
        }

        /// <summary>
        /// Configure silo to use SimpleMessageProvider
        /// </summary>
        public static ISiloHostBuilder AddSimpleMessageStreamProvider(this ISiloHostBuilder builder, string name,
            Action<SimpleMessageStreamProviderOptions> configureOptions)

        {
            return AddSimpleMessageStreamProvider(builder, name, b => b
                .Configure<SimpleMessageStreamProviderOptions>(ob => ob.Configure(configureOptions)));
        }

        /// <summary>
        /// Configure silo to use SimpleMessageProvider
        /// </summary>
        public static ISiloHostBuilder AddSimpleMessageStreamProvider(this ISiloHostBuilder builder, string name,
            Action<OptionsBuilder<SimpleMessageStreamProviderOptions>> configureOptions = null)

        {
            return AddSimpleMessageStreamProvider(builder, name, b => b.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use persistent streams.
        /// </summary>
        public static ISiloBuilder AddPersistentStreams(this ISiloBuilder builder, string name,
            Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory,
            Action<ISiloPersistentStreamConfigurator> configureStream)
        {
            //the constructor wire up DI with all default components of the streams , so need to be called regardless of configureStream null or not
            var streamConfigurator = new SiloPersistentStreamConfigurator(name, configureDelegate => builder.ConfigureServices(configureDelegate), adapterFactory);
            configureStream?.Invoke(streamConfigurator);
            return builder;
        }

        /// <summary>
        /// Configure silo to use SimpleMessageProvider
        /// </summary>
        public static ISiloBuilder AddSimpleMessageStreamProvider(
            this ISiloBuilder builder,
            string name,
            Action<ISimpleMessageStreamConfigurator> configureStream)
        {
            //the constructor wire up DI with all default components of the streams , so need to be called regardless of configureStream null or not
            var streamConfigurator = new SimpleMessageStreamConfigurator(name, configureDelegate => builder.ConfigureServices(configureDelegate));
            configureStream?.Invoke(streamConfigurator);
            return builder;
        }

        /// <summary>
        /// Configure silo to use SimpleMessageProvider
        /// </summary>
        public static ISiloBuilder AddSimpleMessageStreamProvider(this ISiloBuilder builder, string name,
            Action<SimpleMessageStreamProviderOptions> configureOptions)

        {
            return AddSimpleMessageStreamProvider(builder, name, b => b
                .Configure<SimpleMessageStreamProviderOptions>(ob => ob.Configure(configureOptions)));
        }

        /// <summary>
        /// Configure silo to use SimpleMessageProvider
        /// </summary>
        public static ISiloBuilder AddSimpleMessageStreamProvider(this ISiloBuilder builder, string name,
            Action<OptionsBuilder<SimpleMessageStreamProviderOptions>> configureOptions = null)

        {
            return AddSimpleMessageStreamProvider(builder, name, b => b.Configure(configureOptions));
        }
    }
}
