using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Providers;

namespace Orleans.Configuration
{
    internal static class LegacyConfigurationExtensions
    {
        private const int ClusterClientDefaultProviderInitStage = 1000;
        private const int ClusterClientDefaultProviderStartStage = 2000;

        public static IServiceCollection AddLegacyClientConfigurationSupport(this IServiceCollection services, ClientConfiguration configuration)
        {
            if (services.Any(service => service.ServiceType == typeof(ClientConfiguration)))
            {
                throw new InvalidOperationException("Cannot configure legacy ClientConfiguration support twice");
            }

            // these will eventually be removed once our code doesn't depend on the old ClientConfiguration
            services.TryAddSingleton(configuration);
            services.TryAddFromExisting<IMessagingConfiguration, ClientConfiguration>();

            services.Configure<ClusterClientOptions>(options =>
            {
                if (string.IsNullOrWhiteSpace(options.ClusterId) && !string.IsNullOrWhiteSpace(configuration.ClusterId))
                {
                    options.ClusterId = configuration.ClusterId;
                }
            });
            services.TryConfigureFormatter<ClusterClientOptions, ClusterClientOptionsFormatter>();

            // Translate legacy configuration to new Options
            services.Configure<ClientMessagingOptions>(options =>
            {
                CopyCommonMessagingOptions(configuration, options);

                options.ClientSenderBuckets = configuration.ClientSenderBuckets;
            });
            services.TryConfigureFormatter<ClientMessagingOptions, ClientMessagingOptionFormatter>();


            services.Configure<NetworkingOptions>(options => CopyNetworkingOptions(configuration, options));
            services.TryConfigureFormatter<NetworkingOptions, NetworkingOptionFormatter>();

            services.Configure<SerializationProviderOptions>(options =>
            {
                options.SerializationProviders = configuration.SerializationProviders;
                options.FallbackSerializationProvider = configuration.FallbackSerializationProvider;
            });
            services.TryConfigureFormatter<SerializationProviderOptions, SerializationProviderOptionsFormatter>();

            services.Configure<StatisticsOptions>((options) =>
            {
                CopyStatisticsOptions(configuration, options);
            });
            services.TryConfigureFormatter<StatisticsOptions,StatisticOptionsFormatter>();

            // GatewayProvider
            LegacyGatewayListProviderConfigurator.ConfigureServices(configuration, services);

            // Register providers
            LegacyProviderConfigurator<IClusterClientLifecycle>.ConfigureServices(configuration.ProviderConfigurations, services, ClusterClientDefaultProviderInitStage, ClusterClientDefaultProviderStartStage);

            return services;
        }

        internal static void CopyCommonMessagingOptions(IMessagingConfiguration configuration, MessagingOptions options)
        {
            options.ResponseTimeout = configuration.ResponseTimeout;
            options.MaxResendCount = configuration.MaxResendCount;
            options.ResendOnTimeout = configuration.ResendOnTimeout;
            options.DropExpiredMessages = configuration.DropExpiredMessages;
            options.BufferPoolBufferSize = configuration.BufferPoolBufferSize;
            options.BufferPoolMaxSize = configuration.BufferPoolMaxSize;
            options.BufferPoolPreallocationSize = configuration.BufferPoolPreallocationSize;
        }

        internal static void CopyNetworkingOptions(IMessagingConfiguration configuration, NetworkingOptions options)
        {
            options.OpenConnectionTimeout = configuration.OpenConnectionTimeout;
            options.MaxSocketAge = configuration.MaxSocketAge;
        }

        internal static void CopyStatisticsOptions(IStatisticsConfiguration configuration, StatisticsOptions options)
        {
            options.MetricsTableWriteInterval = configuration.StatisticsMetricsTableWriteInterval;
            options.PerfCountersWriteInterval = configuration.StatisticsPerfCountersWriteInterval;
            options.LogWriteInterval = configuration.StatisticsLogWriteInterval;
            options.WriteLogStatisticsToTable = configuration.StatisticsWriteLogStatisticsToTable;
            options.CollectionLevel = configuration.StatisticsCollectionLevel;
        }
    }
}
