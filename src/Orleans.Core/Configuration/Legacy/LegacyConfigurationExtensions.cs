using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Providers;
using Orleans.Configuration.Options;

namespace Orleans.Configuration
{
    internal static class LegacyConfigurationExtensions
    {
        private const int ClusterClientDefaultProviderInitStage = 1000;
        private const int ClusterClientDefaultProviderStartStage = 2000;

        public static IServiceCollection AddLegacyClientConfigurationSupport(this IServiceCollection services, ClientConfiguration configuration = null)
        {
            if (TryGetClientConfiguration(services) != null)
            {
                throw new InvalidOperationException("Cannot configure legacy ClientConfiguration support twice");
            }

            if (configuration == null)
            {
                try
                {
                    configuration = ClientConfiguration.StandardLoad();
                }
                catch (FileNotFoundException)
                {
                    configuration = new ClientConfiguration();
                }
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

            services.Configure<TelemetryOptions>(options =>
            {
                CopyTelemetryOptions(configuration.TelemetryConfiguration, services, options);
            });

            // GatewayProvider
            LegacyGatewayListProviderConfigurator.ConfigureServices(configuration, services);

            // Register providers
            LegacyProviderConfigurator<IClusterClientLifecycle>.ConfigureServices(configuration.ProviderConfigurations, services, ClusterClientDefaultProviderInitStage, ClusterClientDefaultProviderStartStage);

            return services;
        }

        internal static void CopyTelemetryOptions(TelemetryConfiguration telemetryConfiguration, IServiceCollection services, TelemetryOptions options)
        {
            foreach (var consumer in telemetryConfiguration.Consumers)
            {
                services.TryAddSingleton(consumer.ConsumerType, sp => ActivatorUtilities.CreateInstance(sp, consumer.ConsumerType, consumer.Properties.Values?.ToArray() ?? new object[0]));
                options.Consumers.Add(consumer.ConsumerType);
            }
        }
        
        public static ClientConfiguration TryGetClientConfiguration(this IServiceCollection services)
        {
            return services
                .FirstOrDefault(s => s.ServiceType == typeof(ClientConfiguration))
                ?.ImplementationInstance as ClientConfiguration;
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
