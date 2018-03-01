using System;
using Orleans.Configuration;
using Orleans.Providers.GCP.Streams.PubSub;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use PubSub persistent streams.
        /// </summary>
        public static ISiloHostBuilder AddPubSubStreams<TDataAdapter>(
            this ISiloHostBuilder builder,
            string name,
            Action<PubSubStreamOptions> configureOptions)
            where TDataAdapter : IPubSubDataAdapter
        {
            return builder.AddPubSubStreams<TDataAdapter>(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use PubSub persistent streams.
        /// </summary>
        public static ISiloHostBuilder AddPubSubStreams<TDataAdapter>(
            this ISiloHostBuilder builder,
            string name,
            Action<OptionsBuilder<PubSubStreamOptions>> configureOptions = null)
            where TDataAdapter : IPubSubDataAdapter
        {
            return builder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(PubSubBatchContainer).Assembly))
                .ConfigureServices(services =>
                {
                    services.ConfigureNamedOptionForLogging<PubSubStreamOptions>(name)
                        .AddSiloPersistentStreams<PubSubStreamOptions>(name, PubSubAdapterFactory<TDataAdapter>.Create, configureOptions);
                });
        }
    }
}