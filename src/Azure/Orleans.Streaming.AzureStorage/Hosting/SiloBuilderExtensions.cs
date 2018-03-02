using System;
using Orleans.Configuration;
using Orleans.Providers.Streams.AzureQueue;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use azure queue persistent streams.
        /// </summary>
        public static ISiloHostBuilder AddAzureQueueStreams<TDataAdapter>(this ISiloHostBuilder builder, string name, Action<AzureQueueStreamOptions> configureOptions)
           where TDataAdapter : IAzureQueueDataAdapter
        {
            return builder.AddAzureQueueStreams<TDataAdapter>(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use azure queue persistent streams.
        /// </summary>
        public static ISiloHostBuilder AddAzureQueueStreams<TDataAdapter>(this ISiloHostBuilder builder, string name, Action<OptionsBuilder<AzureQueueStreamOptions>> configureOptions = null)
           where TDataAdapter : IAzureQueueDataAdapter
        {
            return builder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(AzureQueueAdapterFactory<>).Assembly))
                .ConfigureServices(services =>
                {
                    services.ConfigureNamedOptionForLogging<AzureQueueStreamOptions>(name)
                        .AddSiloPersistentStreams<AzureQueueStreamOptions>(name, AzureQueueAdapterFactory<TDataAdapter>.Create, configureOptions);
                });
        }
    }
}
