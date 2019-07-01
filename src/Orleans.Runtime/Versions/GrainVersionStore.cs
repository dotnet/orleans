using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Storage;
using Orleans.Versions;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.Versions
{
    internal class GrainVersionStore : IVersionStore
    {
        private readonly IInternalGrainFactory grainFactory;
        private readonly IServiceProvider services;
        private readonly ISiloLifecycle siloLifecycle;
        private readonly string clusterId;
        private readonly bool hasGrainStorage;
        private IVersionStoreGrain StoreGrain => this.grainFactory.GetGrain<IVersionStoreGrain>(this.clusterId);

        public bool IsEnabled => this.hasGrainStorage && this.siloLifecycle.HighestCompletedStage >= ServiceLifecycleStage.BecomeActive;

        public GrainVersionStore(
            IInternalGrainFactory grainFactory,
            ILocalSiloDetails siloDetails,
            IServiceProvider services,
            ISiloLifecycle siloLifecycle)
        {
            this.grainFactory = grainFactory;
            this.services = services;
            this.siloLifecycle = siloLifecycle;
            this.clusterId = siloDetails.ClusterId;
            this.hasGrainStorage = this.services.GetService<IGrainStorage>() != null;
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
