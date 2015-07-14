/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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
            AdjustProvidersDeploymentId(clusterConfig.Globals.ProviderConfigurations, "DataConnectionString", StorageTestConstants.DataConnectionString);
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
            AdjustProvidersDeploymentId(clientConfiguration.ProviderConfigurations, "DataConnectionString", StorageTestConstants.DataConnectionString);
        }

        private static void AdjustProvidersDeploymentId(IEnumerable<KeyValuePair<string, ProviderCategoryConfiguration>> providerConfigurations, string key, string @value)
        {
            if (String.IsNullOrEmpty(@value)) return;

            var providerConfigs = providerConfigurations.Where(kv => kv.Key.Equals(ProviderCategoryConfiguration.STREAM_PROVIDER_CATEGORY_NAME))
                                                        .Select(kv => kv.Value)
                                                        .SelectMany(catagory => catagory.Providers.Values);
            foreach (IProviderConfiguration providerConfig in providerConfigs)
            {
                providerConfig.SetProperty(key, @value);
            }
        }
    }
}
