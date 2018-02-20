
using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Hosting
{
    public static class ClientStreamExtensions
    {
        /// <summary>
        /// Configure silo to use persistent streams.
        /// </summary>
        public static IClientBuilder AddPersistentStreams<TOptions>(this IClientBuilder builder, string name, Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory, Action<TOptions> configureOptions)
            where TOptions : PersistentStreamOptions, new()
        {
            return builder.ConfigureServices(services => services.AddClusterClientPersistentStreams<TOptions>(name, adapterFactory, configureOptions));
        }

        /// <summary>
        /// Configure silo to use persistent streams.
        /// </summary>
        public static IClientBuilder AddPersistentStreams<TOptions>(this IClientBuilder builder, string name, Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory, Action<OptionsBuilder<TOptions>> configureOptions = null)
            where TOptions : PersistentStreamOptions, new()
        {
            return builder.ConfigureServices(services => services.AddClusterClientPersistentStreams<TOptions>(name, adapterFactory, configureOptions));
        }

        /// <summary>
        /// Configure silo to use persistent streams.
        /// </summary>
        public static IServiceCollection AddClusterClientPersistentStreams<TOptions>(this IServiceCollection services, string name, Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory, Action<TOptions> configureOptions)
            where TOptions : PersistentStreamOptions, new()
        {
            return services.AddClusterClientPersistentStreams<TOptions>(name, adapterFactory, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use persistent streams.
        /// </summary>
        public static IServiceCollection AddClusterClientPersistentStreams<TOptions>(this IServiceCollection services, string name, Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory,
            Action<OptionsBuilder<TOptions>> configureOptions = null)
            where TOptions : PersistentStreamOptions, new()
        {
            configureOptions?.Invoke(services.AddOptions<TOptions>(name));
            return services.AddSingletonNamedService<IStreamProvider>(name, PersistentStreamProvider.Create<TOptions>)
                           .AddSingletonNamedService<ILifecycleParticipant<IClusterClientLifecycle>>(name, (s, n) => ((PersistentStreamProvider)s.GetRequiredServiceByName<IStreamProvider>(n)).ParticipateIn<IClusterClientLifecycle>())
                           .AddSingletonNamedService<IQueueAdapterFactory>(name, adapterFactory);
        }
    }
}
