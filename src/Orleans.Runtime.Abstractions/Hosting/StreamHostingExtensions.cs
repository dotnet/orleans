using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Hosting;
using Orleans.Streams;
using Orleans.Providers.Streams.Common;
using Orleans.Providers;
using Orleans.Configuration;

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
    }
}
