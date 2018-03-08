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
        public static ISiloHostBuilder AddPersistentStreams<TOptions>(this ISiloHostBuilder builder, string name, Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory, Action<TOptions> configureOptions)
            where TOptions : PersistentStreamOptions, new()
        {
            return builder.ConfigureServices(services => services.AddSiloPersistentStreams<TOptions>(name, adapterFactory, configureOptions));
        }

        /// <summary>
        /// Configure silo to use persistent streams.
        /// </summary>
        public static ISiloHostBuilder AddPersistentStreams<TOptions>(this ISiloHostBuilder builder, string name, Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory, Action<OptionsBuilder<TOptions>> configureOptions = null)
            where TOptions : PersistentStreamOptions, new()
        {
            return builder.ConfigureServices(services => services.AddSiloPersistentStreams<TOptions>(name, adapterFactory, configureOptions));
        }

        /// <summary>
        /// Configure silo to use persistent streams.
        /// </summary>
        public static IServiceCollection AddSiloPersistentStreams<TOptions>(this IServiceCollection services, string name, Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory, Action<TOptions> configureOptions)
            where TOptions : PersistentStreamOptions, new()
        {
            return services.AddSiloPersistentStreams<TOptions>(name, adapterFactory, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use persistent streams.
        /// </summary>
        public static IServiceCollection AddSiloPersistentStreams<TOptions>(this IServiceCollection services, string name, Func<IServiceProvider, string,IQueueAdapterFactory> adapterFactory,
            Action<OptionsBuilder<TOptions>> configureOptions = null)
            where TOptions : PersistentStreamOptions, new()
        {
            configureOptions?.Invoke(services.AddOptions<TOptions>(name));
            return services.AddSingletonNamedService<IStreamProvider>(name, PersistentStreamProvider.Create<TOptions>)
                           .AddSingletonNamedService<ILifecycleParticipant<ISiloLifecycle>>(name, (s, n) => ((PersistentStreamProvider)s.GetRequiredServiceByName<IStreamProvider>(n)).ParticipateIn<ISiloLifecycle>())
                           .AddSingletonNamedService<IQueueAdapterFactory>(name, adapterFactory)
                           .AddSingletonNamedService(name, (s, n) => s.GetServiceByName<IStreamProvider>(n) as IControllable);
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
            Action<SimpleMessageStreamProviderOptions> configureOptions )
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
