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
using System.Linq;
using Orleans.Providers;
using Orleans.Runtime.Configuration;

namespace UnitTests.Tester.Extensions
{
    public static class GlobalConfigurationTestExtensions
    {
        /// <summary>
        /// This call tweaks the global config with settings specific to a test run.
        /// </summary>
        public static void AdjustForTestEnvironment(this GlobalConfiguration globals)
        {
            // Set the deployment ID for all stream providers
            if (!String.IsNullOrEmpty(globals.DeploymentId))
            {
                var providerConfigs = globals.ProviderConfigurations.Where(kv => kv.Key.Equals(ProviderCategoryConfiguration.STREAM_PROVIDER_CATEGORY_NAME))
                                                                    .Select(kv => kv.Value)
                                                                    .SelectMany(catagory => catagory.Providers.Values);
                foreach (IProviderConfiguration providerConfig in providerConfigs)
                {
                    providerConfig.SetProperty("DeploymentId", globals.DeploymentId);
                }
            }
        }
    }
}
