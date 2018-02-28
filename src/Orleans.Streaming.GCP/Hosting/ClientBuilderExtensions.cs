
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
            return builder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(PubSubBatchContainer).Assembly))
                .ConfigureServices(services => services.AddClusterClientPubSubStreams<TDataAdapter>(name, configureOptions));
        }

        /// <summary>
        /// Configure cluster client to use PubSub persistent streams.
        /// </summary>
        public static IClientBuilder AddPubSubStreams<TDataAdapter>(this IClientBuilder builder, string name, Action<OptionsBuilder<PubSubStreamOptions>> configureOptions = null)
            where TDataAdapter : IPubSubDataAdapter
        {
            return builder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(PubSubBatchContainer).Assembly))
                .ConfigureServices(services => services.AddClusterClientPubSubStreams<TDataAdapter>(name, configureOptions));
        }

        /// <summary>
        /// Configure cluster client to use PubSub persistent streams.
        /// </summary>
        private static void AddClusterClientPubSubStreams<TDataAdapter>(this IServiceCollection services, string name, Action<PubSubStreamOptions> configureOptions)
            where TDataAdapter : IPubSubDataAdapter
        {
            services.AddClusterClientPubSubStreams<TDataAdapter>(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure cluster client to use PubSub persistent streams.
        /// </summary>
        private static void AddClusterClientPubSubStreams<TDataAdapter>(this IServiceCollection services, string name,
            Action<OptionsBuilder<PubSubStreamOptions>> configureOptions = null)
            where TDataAdapter : IPubSubDataAdapter
        {
            services.ConfigureNamedOptionForLogging<PubSubStreamOptions>(name)
                           .AddClusterClientPersistentStreams<PubSubStreamOptions>(name, PubSubAdapterFactory<TDataAdapter>.Create, configureOptions);
        }
    }
}
