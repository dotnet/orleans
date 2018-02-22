using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use memory streams.
        /// </summary>
        public static ISiloHostBuilder AddMemoryStreams<TSerializer>(this ISiloHostBuilder builder, string name, Action<MemoryStreamOptions> configureOptions)
            where TSerializer : class, IMemoryMessageBodySerializer
        {
            return builder.ConfigureServices(services => services.AddSiloMemoryStreams<TSerializer>(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use memory streams.
        /// </summary>
        public static ISiloHostBuilder AddMemoryStreams<TSerializer>(this ISiloHostBuilder builder, string name, Action<OptionsBuilder<MemoryStreamOptions>> configureOptions = null)
            where TSerializer : class, IMemoryMessageBodySerializer
        {
            return builder.ConfigureServices(services => services.AddSiloMemoryStreams<TSerializer>(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use memory streams.
        /// </summary>
        public static IServiceCollection AddSiloMemoryStreams<TSerializer>(this IServiceCollection services, string name, Action<MemoryStreamOptions> configureOptions)
            where TSerializer : class, IMemoryMessageBodySerializer
        {
            return services.AddSiloMemoryStreams<TSerializer>(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use memory streams.
        /// </summary>
        public static IServiceCollection AddSiloMemoryStreams<TSerializer>(this IServiceCollection services, string name,
            Action<OptionsBuilder<MemoryStreamOptions>> configureOptions = null)
            where TSerializer : class, IMemoryMessageBodySerializer
        {
            return services.ConfigureNamedOptionForLogging<MemoryStreamOptions>(name)
                           .AddSiloPersistentStreams<MemoryStreamOptions>(name, MemoryAdapterFactory<TSerializer>.Create, configureOptions);
        }
    }
}
