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

﻿using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Providers;

namespace Orleans.Runtime
{
    internal class BootstrapProviderManager : IProviderManager
    {
        private readonly PluginManager<IBootstrapProvider> pluginManager;
        private readonly string configCategoryName;

        internal BootstrapProviderManager()
        {
            var logger = TraceLogger.GetLogger(this.GetType().Name, TraceLogger.LoggerType.Runtime);
            configCategoryName = ProviderCategoryConfiguration.BOOTSTRAP_PROVIDER_CATEGORY_NAME;
            pluginManager = new PluginManager<IBootstrapProvider>(logger);
        }

        public IProvider GetProvider(string name)
        {
            return pluginManager.GetProvider(name);
        }
        public IList<IBootstrapProvider> GetProviders()
        {
            return pluginManager.GetProviders();
        }

        // Explicitly typed, for backward compat
        public async Task LoadAppBootstrapProviders(
            IDictionary<string, ProviderCategoryConfiguration> configs)
        {
            await pluginManager.LoadAndInitPluginProviders(configCategoryName, configs);
        }



        private class PluginManager<T> : IProviderManager where T : class, IProvider
        {
            private readonly ProviderLoader<T> providerLoader = new ProviderLoader<T>();
            private readonly TraceLogger logger;

            internal PluginManager(TraceLogger logger)
            {
                this.logger = logger;
            }

            public IProvider GetProvider(string name)
            {
                return providerLoader != null ? providerLoader.GetProvider(name) : null;
            }

            public IList<T> GetProviders()
            {
                return providerLoader != null ? providerLoader.GetProviders() : new List<T>();
            }

            internal async Task LoadAndInitPluginProviders(
                string configCategoryName, IDictionary<string, ProviderCategoryConfiguration> configs)
            {
                ProviderCategoryConfiguration categoryConfig;
                if (!configs.TryGetValue(configCategoryName, out categoryConfig)) return;

                var providers = categoryConfig.Providers;
                providerLoader.LoadProviders(providers, this);
                logger.Info(ErrorCode.SiloCallingProviderInit, "Calling Init for {0} classes", typeof(T).Name);

                // Await here to force any errors to show this method name in stack trace, for better diagnostics
                await providerLoader.InitProviders(SiloProviderRuntime.Instance);
            }
        }
    }
}
