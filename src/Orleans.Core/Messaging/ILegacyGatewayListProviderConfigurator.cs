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
                    configurator = CreateInstanceWithParameterlessConstructor<ILegacyGatewayListProviderConfigurator>(
                            Constants.ORLEANS_CLUSTERING_AZURESTORAGE);
                    break;

                case ClientConfiguration.GatewayProviderType.SqlServer:
                    configurator = CreateInstanceWithParameterlessConstructor<ILegacyGatewayListProviderConfigurator>(Constants.ORLEANS_CLUSTERING_ADONET);
                    break;

                case ClientConfiguration.GatewayProviderType.Custom:
                    configurator = CreateInstanceWithParameterlessConstructor<ILegacyGatewayListProviderConfigurator>(
                            clientConfiguration.CustomGatewayProviderAssemblyName);
                    break;

                case ClientConfiguration.GatewayProviderType.ZooKeeper:
                    configurator = CreateInstanceWithParameterlessConstructor<ILegacyGatewayListProviderConfigurator>(
                            Constants.ORLEANS_CLUSTERING_ZOOKEEPER);
                    break;

                case ClientConfiguration.GatewayProviderType.Config:
                    configurator = new LegacyStaticGatewayProviderConfigurator();
                    break;

                default:
                    break;
            }
            configurator?.ConfigureServices(clientConfiguration, services);
        }


        /// <summary>
        /// Create instance for type T in certain assembly, using its parameterless constructor
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="assemblyName"></param>
        /// <returns></returns>
        internal static T CreateInstanceWithParameterlessConstructor<T>(string assemblyName)
        {
            var assembly = Assembly.Load(new AssemblyName(assemblyName));
            var foundType = TypeUtils.GetTypes(assembly, type => typeof(T).IsAssignableFrom(type), null).FirstOrDefault();
            if(foundType == null)
                throw new InvalidOperationException($"type {typeof(T)} was not found in assembly {assembly}");
            return (T)Activator.CreateInstance(foundType, true);
        }
    }

    internal class LegacyStaticGatewayProviderConfigurator : ILegacyGatewayListProviderConfigurator
    {
        public void ConfigureServices(ClientConfiguration clientConfiguration, IServiceCollection services)
        {
            services.UseStaticGatewayListProvider(options =>
            {
                options.Gateways = clientConfiguration.Gateways.Select(ep => ep.ToGatewayUri()).ToList();
            });
        }
    }
}
