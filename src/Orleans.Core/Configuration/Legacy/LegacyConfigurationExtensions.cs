using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    internal static class LegacyConfigurationExtensions
    {
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

            // Translate legacy configuration to new Options
            services.Configure<ClientMessagingOptions>(options =>
            {
                CopyCommonMessagingOptions(configuration, options);

                options.ClientSenderBuckets = configuration.ClientSenderBuckets;
            });

            services.Configure<NetworkingOptions>(options => CopyNetworkingOptions(configuration, options));

            services.Configure<SerializationProviderOptions>(options =>
            {
                options.SerializationProviders = configuration.SerializationProviders;
                options.FallbackSerializationProvider = configuration.FallbackSerializationProvider;
            });

            services.Configure<StatisticsOptions>((options) =>
            {
                CopyStatisticsOptions(configuration, options);
            });

            // GatewayProvider
            LegacyGatewayListProviderConfigurator.ConfigureServices(configuration, services);

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
            options.ProviderName = configuration.StatisticsProviderName;
        }
    }
}
