using System;
using System.Linq;

using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Core.Legacy;
using Orleans.Runtime.Configuration;
using Orleans.Runtime;

namespace Orleans.Messaging
{
    internal class LegacyGatewayListProviderConfigurator
    {
        public static void ConfigureServices(
            ClientConfiguration clientConfiguration,
            IServiceCollection services)
        {
            // If a gateway list provider has already been configured in the service collection, use that instead.
            if (services.Any(reg => reg.ServiceType == typeof(IGatewayListProvider))) return;

            ILegacyGatewayListProviderConfigurator configurator = null;
            switch (clientConfiguration.GatewayProviderToUse)
            {
                case ClientConfiguration.GatewayProviderType.AzureTable:
                    {
                        string assemblyName = Constants.ORLEANS_CLUSTERING_AZURESTORAGE;
                        configurator = LegacyAssemblyLoader.LoadAndCreateInstance<ILegacyGatewayListProviderConfigurator>(assemblyName);
                    }
                    break;

                case ClientConfiguration.GatewayProviderType.AdoNet:
                    {
                        string assemblyName = Constants.ORLEANS_CLUSTERING_ADONET;
                        configurator = LegacyAssemblyLoader.LoadAndCreateInstance<ILegacyGatewayListProviderConfigurator>(assemblyName);
                    }
                    break;

                case ClientConfiguration.GatewayProviderType.Custom:
                    {
                        string assemblyName = clientConfiguration.CustomGatewayProviderAssemblyName;
                        configurator = LegacyAssemblyLoader.LoadAndCreateInstance<ILegacyGatewayListProviderConfigurator>(assemblyName);
                    }
                    break;

                case ClientConfiguration.GatewayProviderType.ZooKeeper:
                    {
                        string assemblyName = Constants.ORLEANS_CLUSTERING_ZOOKEEPER;
                        configurator = LegacyAssemblyLoader.LoadAndCreateInstance<ILegacyGatewayListProviderConfigurator>(assemblyName);
                    }
                    break;

                case ClientConfiguration.GatewayProviderType.Config:
                    configurator = new LegacyStaticGatewayProviderConfigurator();
                    break;

                default:
                    break;
            }
            configurator?.ConfigureServices(clientConfiguration, services);
        }
    }

    internal class LegacyStaticGatewayProviderConfigurator : ILegacyGatewayListProviderConfigurator
    {
        public void ConfigureServices(object clientConfiguration, IServiceCollection services)
        {
            services.Configure<StaticGatewayListProviderOptions>(
                options =>
                {
                    options.Gateways = ((ClientConfiguration)clientConfiguration).Gateways.Select(ep => ep.ToGatewayUri()).ToList();
                });

            services.AddSingleton<IGatewayListProvider, StaticGatewayListProvider>()
                .ConfigureFormatter<StaticGatewayListProviderOptions>();
        }
    }
}
