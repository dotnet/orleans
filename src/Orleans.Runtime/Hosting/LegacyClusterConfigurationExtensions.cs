using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.Scheduler;

namespace Orleans.Hosting
{
    internal static class LegacyClusterConfigurationExtensions
    {
        public static IServiceCollection AddLegacyClusterConfigurationSupport(this IServiceCollection services, ClusterConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            if (services.Any(service => service.ServiceType == typeof(ClusterConfiguration)))
            {
                throw new InvalidOperationException("Cannot configure legacy ClusterConfiguration support twice");
            }

            // these will eventually be removed once our code doesn't depend on the old ClientConfiguration
            services.AddSingleton(configuration);
            services.TryAddSingleton<LegacyConfigurationWrapper>();
            services.TryAddSingleton(sp => sp.GetRequiredService<LegacyConfigurationWrapper>().ClusterConfig.Globals);
            services.TryAddTransient(sp => sp.GetRequiredService<LegacyConfigurationWrapper>().NodeConfig);
            services.TryAddSingleton<Factory<NodeConfiguration>>(
                sp =>
                {
                    var initializationParams = sp.GetRequiredService<LegacyConfigurationWrapper>();
                    return () => initializationParams.NodeConfig;
                });

            services.Configure<SiloOptions>(options =>
            {
                if (string.IsNullOrWhiteSpace(options.ClusterId) && !string.IsNullOrWhiteSpace(configuration.Globals.ClusterId))
                {
                    options.ClusterId = configuration.Globals.ClusterId;
                }

                if (options.ServiceId == Guid.Empty)
                {
                    options.ServiceId = configuration.Globals.ServiceId;
                }
            });

            services.Configure<MultiClusterOptions>(options =>
            {
                var globals = configuration.Globals;
                if (globals.HasMultiClusterNetwork)
                {
                    options.HasMultiClusterNetwork = true;
                    options.BackgroundGossipInterval = globals.BackgroundGossipInterval;
                    options.DefaultMultiCluster = globals.DefaultMultiCluster?.ToList();
                    options.GlobalSingleInstanceNumberRetries = globals.GlobalSingleInstanceNumberRetries;
                    options.GlobalSingleInstanceRetryInterval = globals.GlobalSingleInstanceRetryInterval;
                    options.MaxMultiClusterGateways = globals.MaxMultiClusterGateways;
                    options.UseGlobalSingleInstanceByDefault = globals.UseGlobalSingleInstanceByDefault;
                }
            });

            services.TryAddFromExisting<IMessagingConfiguration, GlobalConfiguration>();

            services.AddOptions<StatisticsOptions>()
                .Configure<NodeConfiguration>((options, nodeConfig) => LegacyConfigurationExtensions.CopyStatisticsOptions(nodeConfig, options));

            // Translate legacy configuration to new Options
            services.Configure<SiloMessagingOptions>(options =>
            {
                LegacyConfigurationExtensions.CopyCommonMessagingOptions(configuration.Globals, options);

                options.SiloSenderQueues = configuration.Globals.SiloSenderQueues;
                options.GatewaySenderQueues = configuration.Globals.GatewaySenderQueues;
                options.MaxForwardCount = configuration.Globals.MaxForwardCount;
                options.ClientDropTimeout = configuration.Globals.ClientDropTimeout;
            });

            services.Configure<NetworkingOptions>(options => LegacyConfigurationExtensions.CopyNetworkingOptions(configuration.Globals, options));

            services.AddOptions<EndpointOptions>()
                .Configure<IOptions<SiloOptions>>((options, siloOptions) =>
                {
                    var nodeConfig = configuration.GetOrCreateNodeConfigurationForSilo(siloOptions.Value.SiloName);
                    if (options.IPAddress == null && string.IsNullOrWhiteSpace(options.HostNameOrIPAddress))
                    {
                        options.IPAddress = nodeConfig.Endpoint.Address;
                        options.Port = nodeConfig.Endpoint.Port;
                    }
                    if (options.ProxyPort == 0 && nodeConfig.ProxyGatewayEndpoint != null)
                    {
                        options.ProxyPort = nodeConfig.ProxyGatewayEndpoint.Port;
                    }
                });

            services.Configure<SerializationProviderOptions>(options =>
            {
                options.SerializationProviders = configuration.Globals.SerializationProviders;
                options.FallbackSerializationProvider = configuration.Globals.FallbackSerializationProvider;
            });

            services.AddOptions<GrainClassOptions>().Configure<IOptions<SiloOptions>>((options, siloOptions) =>
            {
                var nodeConfig = configuration.GetOrCreateNodeConfigurationForSilo(siloOptions.Value.SiloName);
                options.ExcludedGrainTypes.AddRange(nodeConfig.ExcludedGrainTypes);
            });

            LegacyMembershipConfigurator.ConfigureServices(configuration.Globals, services);

            services.AddOptions<SchedulingOptions>().Configure<GlobalConfiguration>((options, config) =>
            {
                options.AllowCallChainReentrancy = config.AllowCallChainReentrancy;
                options.PerformDeadlockDetection = config.PerformDeadlockDetection;
            });
            return services;
        }
    }
}
