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
using System.IO;
using System.Reflection;
using Orleans.Providers;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    internal class DependencyResolverProviderManager : IProviderManager
    {
        private ProviderLoader<IDependencyResolverProvider> dependencyResolverProviderLoader;
        private readonly string providerKind;

        public DependencyResolverProviderManager(string kind)
        {
            providerKind = kind;
        }

        public void LoadProviders(IDictionary<string, ProviderCategoryConfiguration> configs, TraceLogger logger)
        {
            dependencyResolverProviderLoader = new ProviderLoader<IDependencyResolverProvider>();

            if (!configs.ContainsKey(providerKind))
            {
                return;
            }

            var dependencyResolverProviders = configs[providerKind].Providers;

            if (dependencyResolverProviders.Count == 0)
            {
                return;
            }

            if (dependencyResolverProviders.Count > 1)
            {
                throw new ArgumentOutOfRangeException(providerKind + "Providers",
                    string.Format("Only a single {0} provider is supported.", providerKind));
            }

            //
            // Load the assemblies from the Silo's directory, since the dependency resolver's type is not directly referenced,
            // so the ProviderLoader will not find it within the loaded assemblies.
            //

            var siloRoot = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            var directories = new Dictionary<string, SearchOption>
            {
                {siloRoot, SearchOption.TopDirectoryOnly}
            };

            AssemblyLoaderReflectionCriterion[] loadCriteria =
            {
                AssemblyLoaderCriteria.LoadTypesAssignableFrom(
                    typeof (IDependencyResolverProvider))
            };

            var discoveredAssemblyLocations = AssemblyLoader.LoadAssemblies(directories, null, loadCriteria, logger);

            dependencyResolverProviderLoader.LoadProviders(dependencyResolverProviders, this);
        }

        public IProvider GetProvider(string name)
        {
            return dependencyResolverProviderLoader.GetProvider(name, true);
        }
    }
}
