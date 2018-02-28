
using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Providers.Streams.SimpleMessageStream;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Hosting
{
    public static class ClientStreamExtensions
    {
        /// <summary>
        /// Configure silo to use persistent streams.
        /// </summary>
        public static IClientBuilder AddPersistentStreams<TOptions>(
            this IClientBuilder builder,
            string name,
            Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory,
            Action<TOptions> configureOptions)
            where TOptions : PersistentStreamOptions, new()
        {
            return builder.ConfigureServices(services => services.AddClusterClientPersistentStreams<TOptions>(name, adapterFactory, configureOptions));
        }

        /// <summary>
        /// Configure silo to use persistent streams.
        /// </summary>
        public static IClientBuilder AddPersistentStreams<TOptions>(
            this IClientBuilder builder,
            string name,
            Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory,
            Action<OptionsBuilder<TOptions>> configureOptions = null)
            where TOptions : PersistentStreamOptions, new()
        {
            return builder.ConfigureServices(services => services.AddClusterClientPersistentStreams<TOptions>(name, adapterFactory, configureOptions));
        }

        /// <summary>
        /// Configure silo to use persistent streams.
        /// </summary>
        public static void AddClusterClientPersistentStreams<TOptions>(
            this IServiceCollection services,
            string name,
            Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory,
            Action<TOptions> configureOptions)
            where TOptions : PersistentStreamOptions, new()
        {
            services.AddClusterClientPersistentStreams<TOptions>(name, adapterFactory, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use persistent streams.
        /// </summary>
        public static void AddClusterClientPersistentStreams<TOptions>(
            this IServiceCollection services,
            string name,
            Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory,
            Action<OptionsBuilder<TOptions>> configureOptions = null)
            where TOptions : PersistentStreamOptions, new()
        {
            configureOptions?.Invoke(services.AddOptions<TOptions>(name));
            services.AddSingletonNamedService<IStreamProvider>(name, PersistentStreamProvider.Create<TOptions>)
                .AddSingletonNamedService<ILifecycleParticipant<IClusterClientLifecycle>>(name,
                    (s, n) => ((PersistentStreamProvider) s.GetRequiredServiceByName<IStreamProvider>(n)).ParticipateIn<IClusterClientLifecycle>())
                .AddSingletonNamedService<IQueueAdapterFactory>(name, adapterFactory);
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
