using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Configuration.Options;
using Orleans.Runtime.Configuration;
using Orleans.Runtime;

namespace Orleans.Messaging
{
    /// <summary>
    /// LegacyGatewayProviderConfigurator configure GatewayListProvider in the legacy way, which is from ClientConfiguration
    /// </summary>
    public interface ILegacyGatewayListProviderConfigurator
    {
        void ConfigureServices(ClientConfiguration configuration, IServiceCollection services);
    }

    internal class LegacyGatewayProviderConfigurator
    {
        public static void ConfigureServices(ClientConfiguration clientConfiguration,
            IServiceCollection services)
        {
            ILegacyGatewayListProviderConfigurator configurator = null;
            switch (clientConfiguration.GatewayProviderToUse)
            {
                case ClientConfiguration.GatewayProviderType.AzureTable:
                    configurator = AssemblyLoader.CreateInstance<ILegacyGatewayListProviderConfigurator>(
                            Constants.ORLEANS_AZURE_UTILS_DLL);
                    break;

                case ClientConfiguration.GatewayProviderType.SqlServer:
                    configurator = AssemblyLoader
                        .CreateInstance<ILegacyGatewayListProviderConfigurator>(Constants.ORLEANS_SQL_UTILS_DLL);
                    break;

                case ClientConfiguration.GatewayProviderType.Custom:
                    configurator = AssemblyLoader.CreateInstance<ILegacyGatewayListProviderConfigurator>(
                            clientConfiguration.CustomGatewayProviderAssemblyName);
                    break;

                case ClientConfiguration.GatewayProviderType.ZooKeeper:
                    configurator = AssemblyLoader.CreateInstance<ILegacyGatewayListProviderConfigurator>(
                            Constants.ORLEANS_ZOOKEEPER_UTILS_DLL);
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
        public void ConfigureServices(ClientConfiguration clientConfiguration, IServiceCollection services)
        {
            services.UseStaticGatewayProvider(options =>
            {
                options.GatewayListRefreshPeriod = clientConfiguration.GatewayListRefreshPeriod;
                options.Gateways = clientConfiguration.Gateways.Select(ep => ep.ToGatewayUri()).ToList();
            });
        }
    }
}
