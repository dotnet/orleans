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

        public Task SetCompatibilityStrategy(CompatibilityStrategy strategy)
            => SetCompatibilityStrategy(strategy, CancellationToken.None);

        public async Task SetCompatibilityStrategy(CompatibilityStrategy strategy, CancellationToken cancellationToken)
        {
            ThrowIfNotEnabled();
            await StoreGrain.SetCompatibilityStrategy(strategy, cancellationToken);
        }

        public Task SetSelectorStrategy(VersionSelectorStrategy strategy)
            => SetSelectorStrategy(strategy, CancellationToken.None);

        public async Task SetSelectorStrategy(VersionSelectorStrategy strategy, CancellationToken cancellationToken)
        {
            ThrowIfNotEnabled();
            await StoreGrain.SetSelectorStrategy(strategy, cancellationToken);
        }

        public Task SetCompatibilityStrategy(GrainInterfaceType interfaceType, CompatibilityStrategy strategy)
            => SetCompatibilityStrategy(interfaceType, strategy, CancellationToken.None);

        public async Task SetCompatibilityStrategy(
            GrainInterfaceType interfaceType,
            CompatibilityStrategy strategy,
            CancellationToken cancellationToken)
        {
            ThrowIfNotEnabled();
            await StoreGrain.SetCompatibilityStrategy(interfaceType, strategy, cancellationToken);
        }

        public Task SetSelectorStrategy(GrainInterfaceType interfaceType, VersionSelectorStrategy strategy)
            => SetSelectorStrategy(interfaceType, strategy, CancellationToken.None);

        public async Task SetSelectorStrategy(
            GrainInterfaceType interfaceType,
            VersionSelectorStrategy strategy,
            CancellationToken cancellationToken)
        {
            ThrowIfNotEnabled();
            await StoreGrain.SetSelectorStrategy(interfaceType, strategy, cancellationToken);
        }

        public Task<Dictionary<GrainInterfaceType, CompatibilityStrategy>> GetCompatibilityStrategies()
            => GetCompatibilityStrategies(CancellationToken.None);

        public async Task<Dictionary<GrainInterfaceType, CompatibilityStrategy>> GetCompatibilityStrategies(
            CancellationToken cancellationToken)
        {
            ThrowIfNotEnabled();
            return await StoreGrain.GetCompatibilityStrategies(cancellationToken);
        }

        public Task<Dictionary<GrainInterfaceType, VersionSelectorStrategy>> GetSelectorStrategies()
            => GetSelectorStrategies(CancellationToken.None);

        public async Task<Dictionary<GrainInterfaceType, VersionSelectorStrategy>> GetSelectorStrategies(
            CancellationToken cancellationToken)
        {
            ThrowIfNotEnabled();
            return await StoreGrain.GetSelectorStrategies(cancellationToken);
        }

        public Task<CompatibilityStrategy?> GetCompatibilityStrategy()
            => GetCompatibilityStrategy(CancellationToken.None);

        public async Task<CompatibilityStrategy?> GetCompatibilityStrategy(CancellationToken cancellationToken)
        {
            ThrowIfNotEnabled();
            return await StoreGrain.GetCompatibilityStrategy(cancellationToken);
        }

        public Task<VersionSelectorStrategy?> GetSelectorStrategy()
            => GetSelectorStrategy(CancellationToken.None);

        public async Task<VersionSelectorStrategy?> GetSelectorStrategy(CancellationToken cancellationToken)
        {
            ThrowIfNotEnabled();
            return await StoreGrain.GetSelectorStrategy(cancellationToken);
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
