using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;
using LivenessProviderType = Orleans.Runtime.Configuration.GlobalConfiguration.LivenessProviderType;

namespace Orleans.Runtime.MembershipService
{
    internal static class LegacyMembershipConfigurator
    {
        /// <summary>
        /// Legacy way to create membership table. Will need to move to a legacy package in the future
        /// </summary>
        /// <returns></returns>
        internal static void ConfigureServices(GlobalConfiguration configuration, IServiceCollection services)
        {
            ILegacyMembershipConfigurator configurator = null;
            switch (configuration.LivenessType)
            {
                case LivenessProviderType.MembershipTableGrain:
                    configurator = new LegacyGrainBasedMembershipConfigurator();
                    break;
                case LivenessProviderType.SqlServer:
                    configurator = LegacyGatewayListProviderConfigurator.CreateInstanceWithParameterlessConstructor<ILegacyMembershipConfigurator>(Constants.ORLEANS_CLUSTERING_ADONET);
                    break;
                case LivenessProviderType.AzureTable:
                    configurator = LegacyGatewayListProviderConfigurator.CreateInstanceWithParameterlessConstructor<ILegacyMembershipConfigurator>(Constants.ORLEANS_CLUSTERING_AZURESTORAGE);
                    break;
                case LivenessProviderType.ZooKeeper:
                    configurator = LegacyGatewayListProviderConfigurator.CreateInstanceWithParameterlessConstructor<ILegacyMembershipConfigurator>(Constants.ORLEANS_CLUSTERING_ZOOKEEPER);
                    break;
                case LivenessProviderType.Custom:
                    configurator = LegacyGatewayListProviderConfigurator.CreateInstanceWithParameterlessConstructor<ILegacyMembershipConfigurator>(configuration.MembershipTableAssembly);
                    break;
                default:
                    break;
            }

            configurator?.ConfigureServices(configuration, services);
        }
        private class LegacyGrainBasedMembershipConfigurator : ILegacyMembershipConfigurator
        {
            public void ConfigureServices(GlobalConfiguration configuration, IServiceCollection services)
            {
                services.UseDevelopmentMembership(options => CopyGlobalGrainBasedMembershipOptions(configuration, options));
            }

            private static void CopyGlobalGrainBasedMembershipOptions(GlobalConfiguration configuration, DevelopmentMembershipOptions options)
            {
                if (configuration.SeedNodes?.Count > 0)
                {
                    options.PrimarySiloEndpoint = configuration.SeedNodes?.FirstOrDefault();
                }
            }
        }
    }
}
