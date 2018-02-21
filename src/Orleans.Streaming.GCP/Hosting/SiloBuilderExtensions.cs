using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers.GCP.Streams.PubSub;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use PubSub persistent streams.
        /// </summary>
        public static ISiloHostBuilder AddPubSubStreams<TDataAdapter>(this ISiloHostBuilder builder, string name, Action<PubSubStreamOptions> configureOptions)
            where TDataAdapter : IPubSubDataAdapter
        {
            return builder.ConfigureServices(services => services.AddSiloPubSubStreams<TDataAdapter>(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use PubSub persistent streams.
        /// </summary>
        public static ISiloHostBuilder AddPubSubStreams<TDataAdapter>(this ISiloHostBuilder builder, string name, Action<OptionsBuilder<PubSubStreamOptions>> configureOptions = null)
            where TDataAdapter : IPubSubDataAdapter
        {
            return builder.ConfigureServices(services => services.AddSiloPubSubStreams<TDataAdapter>(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use PubSub persistent streams.
        /// </summary>
        public static IServiceCollection AddSiloPubSubStreams<TDataAdapter>(this IServiceCollection services, string name, Action<PubSubStreamOptions> configureOptions)
            where TDataAdapter : IPubSubDataAdapter
        {
            return services.AddSiloPubSubStreams<TDataAdapter>(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use PubSub persistent streams.
        /// </summary>
        public static IServiceCollection AddSiloPubSubStreams<TDataAdapter>(this IServiceCollection services, string name,
            Action<OptionsBuilder<PubSubStreamOptions>> configureOptions = null)
            where TDataAdapter : IPubSubDataAdapter
        {
            return services.ConfigureNamedOptionForLogging<PubSubStreamOptions>(name)
                           .AddSiloPersistentStreams<PubSubStreamOptions>(name, PubSubAdapterFactory< TDataAdapter>.Create, configureOptions);
        }
    }
}
