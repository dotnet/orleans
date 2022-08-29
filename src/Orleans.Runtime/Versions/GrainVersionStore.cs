using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Storage;
using Orleans.Versions;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using Microsoft.Extensions.DependencyInjection;
using System.Threading;

namespace Orleans.Runtime.Versions
{
    internal class GrainVersionStore : IVersionStore, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly IInternalGrainFactory grainFactory;
        private readonly IServiceProvider services;
        private readonly string clusterId;
        private IVersionStoreGrain StoreGrain => this.grainFactory.GetGrain<IVersionStoreGrain>(this.clusterId);

        public bool IsEnabled { get; private set; }

        public GrainVersionStore(IInternalGrainFactory grainFactory, ILocalSiloDetails siloDetails, IServiceProvider services)
        {
            this.grainFactory = grainFactory;
            this.services = services;
            this.clusterId = siloDetails.ClusterId;
            this.IsEnabled = false;
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

        public async Task SetCompatibilityStrategy(GrainInterfaceType interfaceType, CompatibilityStrategy strategy)
        {
            ThrowIfNotEnabled();
            await StoreGrain.SetCompatibilityStrategy(interfaceType, strategy);
        }

        public async Task SetSelectorStrategy(GrainInterfaceType interfaceType, VersionSelectorStrategy strategy)
        {
            ThrowIfNotEnabled();
            await StoreGrain.SetSelectorStrategy(interfaceType, strategy);
        }

        public async Task<Dictionary<GrainInterfaceType, CompatibilityStrategy>> GetCompatibilityStrategies()
        {
            ThrowIfNotEnabled();
            return await StoreGrain.GetCompatibilityStrategies();
        }

        public async Task<Dictionary<GrainInterfaceType, VersionSelectorStrategy>> GetSelectorStrategies()
        {
            ThrowIfNotEnabled();
            return await StoreGrain.GetSelectorStrategies();
        }

        public async Task<CompatibilityStrategy> GetCompatibilityStrategy()
        {
            ThrowIfNotEnabled();
            return await StoreGrain.GetCompatibilityStrategy();
        }

        public async Task<VersionSelectorStrategy> GetSelectorStrategy()
        {
            ThrowIfNotEnabled();
            return await StoreGrain.GetSelectorStrategy();
        }

        private void ThrowIfNotEnabled()
        {
            if (!IsEnabled) ThrowDisabled();

            static void ThrowDisabled() => throw new OrleansException("Version store not enabled, make sure the store is configured");
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe<GrainVersionStore>(ServiceLifecycleStage.ApplicationServices, this.OnStart);
        }

        private Task OnStart(CancellationToken token)
        {
            this.IsEnabled = this.services.GetService<IGrainStorage>() != null;
            return Task.CompletedTask;
        }
    }
}
