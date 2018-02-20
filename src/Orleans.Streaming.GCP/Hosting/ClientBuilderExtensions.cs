
using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Providers.GCP.Streams.PubSub;

namespace Orleans.Hosting
{
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Configure cluster client to use PubSub persistent streams.
        /// </summary>
        public static IClientBuilder AddPubSubStreams<TDataAdapter>(this IClientBuilder builder, string name, Action<PubSubStreamOptions> configureOptions)
            where TDataAdapter : IPubSubDataAdapter
        {
            return builder.ConfigureServices(services => services.AddClusterClientPubSubStreams<TDataAdapter>(name, configureOptions));
        }

        /// <summary>
        /// Configure cluster client to use PubSub persistent streams.
        /// </summary>
        public static IClientBuilder AddPubSubStreams<TDataAdapter>(this IClientBuilder builder, string name, Action<OptionsBuilder<PubSubStreamOptions>> configureOptions = null)
            where TDataAdapter : IPubSubDataAdapter
        {
            return builder.ConfigureServices(services => services.AddClusterClientPubSubStreams<TDataAdapter>(name, configureOptions));
        }

        /// <summary>
        /// Configure cluster client to use PubSub persistent streams.
        /// </summary>
        public static IServiceCollection AddClusterClientPubSubStreams<TDataAdapter>(this IServiceCollection services, string name, Action<PubSubStreamOptions> configureOptions)
            where TDataAdapter : IPubSubDataAdapter
        {
            return services.AddClusterClientPubSubStreams<TDataAdapter>(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure cluster client to use PubSub persistent streams.
        /// </summary>
        public static IServiceCollection AddClusterClientPubSubStreams<TDataAdapter>(this IServiceCollection services, string name,
            Action<OptionsBuilder<PubSubStreamOptions>> configureOptions = null)
            where TDataAdapter : IPubSubDataAdapter
        {
            return services.ConfigureNamedOptionForLogging<PubSubStreamOptions>(name)
                           .AddClusterClientPersistentStreams<PubSubStreamOptions>(name, PubSubAdapterFactory<TDataAdapter>.Create, configureOptions);
        }
    }
}
