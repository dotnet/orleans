using System;
using Orleans.Configuration;
using Orleans.Providers;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use memory streams.
        /// </summary>
        public static ISiloHostBuilder AddMemoryStreams<TSerializer>(
            this ISiloHostBuilder builder,
            string name,
            Action<MemoryStreamOptions> configureOptions)
            where TSerializer : class, IMemoryMessageBodySerializer
        {
            return builder.AddMemoryStreams<TSerializer>(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use memory streams.
        /// </summary>
        public static ISiloHostBuilder AddMemoryStreams<TSerializer>(
            this ISiloHostBuilder builder,
            string name,
            Action<OptionsBuilder<MemoryStreamOptions>> configureOptions = null)
            where TSerializer : class, IMemoryMessageBodySerializer
        {
            return builder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(MemoryAdapterFactory<>).Assembly))
                .ConfigureServices(services =>
                {
                    services
                        .ConfigureNamedOptionForLogging<MemoryStreamOptions>(name)
                        .AddSiloPersistentStreams<MemoryStreamOptions>(name, MemoryAdapterFactory<TSerializer>.Create, configureOptions);
                });
        }
    }
}
