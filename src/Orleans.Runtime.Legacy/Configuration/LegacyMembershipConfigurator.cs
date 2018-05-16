using System;
using System.Linq;

using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Core.Legacy;
using Orleans.Hosting;
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
        internal static void ConfigureServices(GlobalConfiguration configuration, ISiloHostBuilder builder)
        {
            ILegacyMembershipConfigurator configurator = null;
            switch (configuration.LivenessType)
            {
                case LivenessProviderType.MembershipTableGrain:
                    configurator = new LegacyGrainBasedMembershipConfigurator();
                    break;
                case LivenessProviderType.AdoNet:
                    {
                        string assemblyName = Constants.ORLEANS_CLUSTERING_ADONET;
                        configurator = LegacyAssemblyLoader.LoadAndCreateInstance<ILegacyMembershipConfigurator>(assemblyName);
                    }
                    break;
                case LivenessProviderType.AzureTable:
                    {
                        string assemblyName = Constants.ORLEANS_CLUSTERING_AZURESTORAGE;
                        configurator = LegacyAssemblyLoader.LoadAndCreateInstance<ILegacyMembershipConfigurator>(assemblyName);
                    }
                    break;
                case LivenessProviderType.ZooKeeper:
                    {
                        string assemblyName = Constants.ORLEANS_CLUSTERING_ZOOKEEPER;
                        configurator = LegacyAssemblyLoader.LoadAndCreateInstance<ILegacyMembershipConfigurator>(assemblyName);
                    }
                    break;
                case LivenessProviderType.Custom:
                    {
                        string assemblyName = configuration.MembershipTableAssembly;
                        configurator = LegacyAssemblyLoader.LoadAndCreateInstance<ILegacyMembershipConfigurator>(assemblyName);
                    }
                    break;
                default:
                    break;
            }

            configurator?.Configure(configuration, builder);
        }

        private class LegacyGrainBasedMembershipConfigurator : ILegacyMembershipConfigurator
        {
            public void Configure(object configuration, ISiloHostBuilder builder)
            {
                if (!(configuration is GlobalConfiguration config))
                {
                    throw new ArgumentException($"{nameof(GlobalConfiguration)} expected", nameof(configuration));
                }

                builder.UseDevelopmentClustering(options =>
                {
                    if (config.SeedNodes?.Count > 0)
                    {
                        options.PrimarySiloEndpoint = config.SeedNodes?.FirstOrDefault();
                    }
                });
            }
        }
    }
}
