﻿using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;

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
            services.TryAddSingleton<SiloInitializationParameters>();
            services.TryAddFromExisting<ILocalSiloDetails, SiloInitializationParameters>();
            services.TryAddSingleton(sp => sp.GetRequiredService<SiloInitializationParameters>().ClusterConfig);
            services.TryAddSingleton(sp => sp.GetRequiredService<SiloInitializationParameters>().ClusterConfig.Globals);
            services.TryAddTransient(sp => sp.GetRequiredService<SiloInitializationParameters>().NodeConfig);
            services.TryAddSingleton<Factory<NodeConfiguration>>(
                sp =>
                {
                    var initializationParams = sp.GetRequiredService<SiloInitializationParameters>();
                    return () => initializationParams.NodeConfig;
                });

            services.Configure<SiloOptions>(options =>
            {
#pragma warning disable CS0618 // Type or member is obsolete
                if (string.IsNullOrWhiteSpace(options.ClusterId) && !string.IsNullOrWhiteSpace(configuration.Globals.DeploymentId))
                {
                    options.ClusterId = configuration.Globals.DeploymentId;
                }

                if (configuration.Globals.HasMultiClusterNetwork)
                {
                    options.HasMultiClusterNetwork = true;
                }
#pragma warning restore CS0618 // Type or member is obsolete
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
            return services;
        }
    }
}
