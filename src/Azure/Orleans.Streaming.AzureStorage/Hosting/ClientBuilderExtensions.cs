using System;
using Orleans.Configuration;
using Orleans.Providers.Streams.AzureQueue;

namespace Orleans.Hosting
{
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Configure cluster client to use azure queue persistent streams.
        /// </summary>
        public static IClientBuilder AddAzureQueueStreams<TDataAdapter>(
            this IClientBuilder builder,
            string name,
            Action<AzureQueueStreamOptions> configureOptions)
            where TDataAdapter : IAzureQueueDataAdapter
        {
            return builder.AddAzureQueueStreams<TDataAdapter>(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure cluster client to use azure queue persistent streams.
        /// </summary>
        public static IClientBuilder AddAzureQueueStreams<TDataAdapter>(this IClientBuilder builder,
            string name,
            Action<OptionsBuilder<AzureQueueStreamOptions>> configureOptions = null)
            where TDataAdapter : IAzureQueueDataAdapter
        {
            return builder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(AzureQueueAdapterFactory<>).Assembly))
                .ConfigureServices(services =>
                {
                    services.ConfigureNamedOptionForLogging<AzureQueueStreamOptions>(name)
                        .AddClusterClientPersistentStreams<AzureQueueStreamOptions>(name, AzureQueueAdapterFactory<TDataAdapter>.Create, configureOptions);
                });
        }
    }
}
