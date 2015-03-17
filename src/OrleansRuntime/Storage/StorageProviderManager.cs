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

﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Providers;
using Orleans.Storage;

namespace Orleans.Runtime.Storage
{
    internal class StorageProviderManager : IStorageProviderManager, IStorageProviderRuntime
    {
        private ProviderLoader<IStorageProvider> storageProviderLoader;
        private IProviderRuntime providerRuntime;
        
        internal Task LoadStorageProviders(IDictionary<string, ProviderCategoryConfiguration> configs)
        {
            storageProviderLoader = new ProviderLoader<IStorageProvider>();
            providerRuntime = SiloProviderRuntime.Instance;

            if (!configs.ContainsKey(ProviderCategoryConfiguration.STORAGE_PROVIDER_CATEGORY_NAME))
                return TaskDone.Done;

            storageProviderLoader.LoadProviders(configs[ProviderCategoryConfiguration.STORAGE_PROVIDER_CATEGORY_NAME].Providers, this);
            return storageProviderLoader.InitProviders(providerRuntime);
        }

        internal void UnloadStorageProviders()
        {
            foreach (var provider in storageProviderLoader.GetProviders())
            {
                var disp = provider as IDisposable;
                if (disp != null)
                    disp.Dispose();
            }
        }

        public int GetNumLoadedProviders()
        {
            return storageProviderLoader.GetNumLoadedProviders();
        }

        public Logger GetLogger(string loggerName)
        {
            return TraceLogger.GetLogger(loggerName, TraceLogger.LoggerType.Provider);
        }

        public Guid ServiceId
        {
            get { return providerRuntime.ServiceId; }
        }

        /// <summary>
        /// Get list of providers loaded in this silo.
        /// </summary>
        /// <returns></returns>
        public IEnumerable<string> GetProviderNames()
        {
            var providers = storageProviderLoader.GetProviders();
            return providers.Select(p => p.GetType().FullName).ToList();
        }

        public IStorageProvider GetDefaultProvider()
        {
            return storageProviderLoader.GetDefaultProvider(Constants.DEFAULT_STORAGE_PROVIDER_NAME);
        }

        public bool TryGetProvider(string name, out IStorageProvider provider, bool caseInsensitive = false)
        {
            return storageProviderLoader.TryGetProvider(name, out provider, caseInsensitive);
        }

        public IProvider GetProvider(string name)
        {
            return storageProviderLoader.GetProvider(name, true);
        }

        // used only for testing
        internal Task LoadEmptyStorageProviders()
        {
            storageProviderLoader = new ProviderLoader<IStorageProvider>();
            providerRuntime = ClientProviderRuntime.Instance;

            storageProviderLoader.LoadProviders(new Dictionary<string, IProviderConfiguration>(), this);
            return storageProviderLoader.InitProviders(providerRuntime);
        }

        // used only for testing
        internal async Task AddAndInitProvider(string name, IStorageProvider provider, IProviderConfiguration config=null)
        {
            await provider.Init(name, this, config);
            storageProviderLoader.AddProvider(name, provider, config);
        }
    }
}