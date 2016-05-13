using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Providers;
using Orleans.Runtime.Configuration;

namespace Orleans.TestingHost.Extensions
{
    public static class TestConfigurationExtensions
    {
        /// <summary>
        /// This call tweaks the cluster config with settings specific to a test run.
        /// </summary>
        public static void AdjustForTestEnvironment(this ClusterConfiguration clusterConfig)
        {
            if (clusterConfig == null)
            {
                throw new ArgumentNullException("clusterConfig");
            }

            AdjustProvidersDeploymentId(clusterConfig.Globals.ProviderConfigurations, "DeploymentId", clusterConfig.Globals.DeploymentId);

            if (string.IsNullOrEmpty(clusterConfig.Globals.DataConnectionString))
            {
                clusterConfig.Globals.DataConnectionString = StorageTestConstants.DataConnectionString;
            }

            AdjustProvidersDeploymentId(clusterConfig.Globals.ProviderConfigurations, "DataConnectionString", clusterConfig.Globals.DataConnectionString);
        }

        /// <summary>
        /// This call tweaks the client config with settings specific to a test run.
        /// </summary>
        public static void AdjustForTestEnvironment(this ClientConfiguration clientConfiguration)
        {
            if (clientConfiguration == null)
            {
                throw new ArgumentNullException("clientConfiguration");
            }

            AdjustProvidersDeploymentId(clientConfiguration.ProviderConfigurations, "DeploymentId", clientConfiguration.DeploymentId);
            if (string.IsNullOrEmpty(clientConfiguration.DataConnectionString))
            {
                clientConfiguration.DataConnectionString = StorageTestConstants.DataConnectionString;
            }

            AdjustProvidersDeploymentId(clientConfiguration.ProviderConfigurations, "DataConnectionString", clientConfiguration.DataConnectionString);
        }

        private static void AdjustProvidersDeploymentId(IEnumerable<KeyValuePair<string, ProviderCategoryConfiguration>> providerConfigurations, string key, string @value)
        {
            if (String.IsNullOrEmpty(@value)) return;

            var providerConfigs = providerConfigurations.Where(kv => kv.Key.Equals(ProviderCategoryConfiguration.STREAM_PROVIDER_CATEGORY_NAME) ||
                                                                    kv.Key.Equals(ProviderCategoryConfiguration.STORAGE_PROVIDER_CATEGORY_NAME))
                                                        .Select(kv => kv.Value)
                                                        .SelectMany(catagory => catagory.Providers.Values);
            foreach (IProviderConfiguration providerConfig in providerConfigs)
            {
                providerConfig.SetProperty(key, @value);
            }
        }
    }
}
