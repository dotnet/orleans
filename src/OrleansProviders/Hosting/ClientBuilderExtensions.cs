
using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Providers;

namespace Orleans.Hosting
{
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Configure cluster client to use memory streams.
        /// </summary>
        public static IClientBuilder AddMemoryStreams<TSerializer>(this IClientBuilder builder, string name, Action<MemoryStreamOptions> configureOptions)
            where TSerializer : class, IMemoryMessageBodySerializer
        {
            return builder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(MemoryAdapterFactory<>).Assembly))
                .ConfigureServices(services => services.AddClusterClientMemoryStreams<TSerializer>(name, configureOptions));
        }

        /// <summary>
        /// Configure cluster client to use memory streams.
        /// </summary>
        public static IClientBuilder AddMemoryStreams<TSerializer>(this IClientBuilder builder, string name, Action<OptionsBuilder<MemoryStreamOptions>> configureOptions = null)
            where TSerializer : class, IMemoryMessageBodySerializer
        {
            return builder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(MemoryAdapterFactory<>).Assembly))
                .ConfigureServices(services => services.AddClusterClientMemoryStreams<TSerializer>(name, configureOptions));
        }

        /// <summary>
        /// Configure cluster client to use memory streams.
        /// </summary>
        private static void AddClusterClientMemoryStreams<TSerializer>(this IServiceCollection services, string name, Action<MemoryStreamOptions> configureOptions)
            where TSerializer : class, IMemoryMessageBodySerializer
        {
            services.AddClusterClientMemoryStreams<TSerializer>(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure cluster client to use memory streams.
        /// </summary>
        private static void AddClusterClientMemoryStreams<TSerializer>(this IServiceCollection services,
            string name,
            Action<OptionsBuilder<MemoryStreamOptions>> configureOptions = null)
            where TSerializer : class, IMemoryMessageBodySerializer
        {
            services.ConfigureNamedOptionForLogging<MemoryStreamOptions>(name)
                .AddClusterClientPersistentStreams<MemoryStreamOptions>(name, MemoryAdapterFactory<TSerializer>.Create, configureOptions);
        }
    }
}
