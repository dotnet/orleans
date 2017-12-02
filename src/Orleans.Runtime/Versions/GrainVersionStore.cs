﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Runtime.Configuration;
using Orleans.Storage;
using Orleans.Versions;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using Orleans.Providers;

namespace Orleans.Runtime.Versions
{
    internal class GrainVersionStore : IVersionStore
    {
        private readonly IInternalGrainFactory grainFactory;
        private readonly string clusterId;
        private IVersionStoreGrain StoreGrain => this.grainFactory.GetGrain<IVersionStoreGrain>(this.clusterId);

        public bool IsEnabled { get; private set; }

        public GrainVersionStore(IInternalGrainFactory grainFactory, IOptions<SiloOptions> siloOptions)
        {
            this.grainFactory = grainFactory;
            this.clusterId = siloOptions.Value.ClusterId;
            this.IsEnabled = false;
        }

        public void SetStorageManager(IStorageProviderManager storageProviderManager)
        {
            IStorageProvider unused;
            IsEnabled = storageProviderManager.TryGetProvider(
                ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME,
                out unused);
        }

        public async Task SetCompatibilityStrategy(CompatibilityStrategy strategy)
        {
            ThrowIfNotEnabled();
            await StoreGrain.SetCompatibilityStrategy(strategy);
        }

        public async Task SetSelectorStrategy(VersionSelectorStrategy strategy)
        {
            ThrowIfNotEnabled();
            await StoreGrain.SetSelectorStrategy(strategy);
        }

        public async Task SetCompatibilityStrategy(int interfaceId, CompatibilityStrategy strategy)
        {
            ThrowIfNotEnabled();
            await StoreGrain.SetCompatibilityStrategy(interfaceId, strategy);
        }

        public async Task SetSelectorStrategy(int interfaceId, VersionSelectorStrategy strategy)
        {
            ThrowIfNotEnabled();
            await StoreGrain.SetSelectorStrategy(interfaceId, strategy);
        }

        public async Task<Dictionary<int, CompatibilityStrategy>> GetCompatibilityStrategies()
        {
            ThrowIfNotEnabled();
            return await StoreGrain.GetCompatibilityStrategies();
        }

        public async Task<Dictionary<int, VersionSelectorStrategy>> GetSelectorStrategies()
        {
            ThrowIfNotEnabled();
            return await StoreGrain.GetSelectorStrategies();
        }

        public async Task<CompatibilityStrategy> GetCompatibilityStrategy()
        {
            return await StoreGrain.GetCompatibilityStrategy();
        }

        public async Task<VersionSelectorStrategy> GetSelectorStrategy()
        {
            return await StoreGrain.GetSelectorStrategy();
        }

        private void ThrowIfNotEnabled()
        {
            if (!IsEnabled)
                throw new OrleansException("Version store not enabled, make sure the store is configured");
        }
    }
}
