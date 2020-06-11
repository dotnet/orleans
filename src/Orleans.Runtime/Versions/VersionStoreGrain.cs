using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Versions;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

namespace Orleans.Runtime.Versions
{
    internal interface IVersionStoreGrain : IGrainWithStringKey
    {
        Task<Dictionary<GrainInterfaceType, CompatibilityStrategy>> GetCompatibilityStrategies();
        Task<Dictionary<GrainInterfaceType, VersionSelectorStrategy>> GetSelectorStrategies();
        Task<CompatibilityStrategy> GetCompatibilityStrategy();
        Task<VersionSelectorStrategy> GetSelectorStrategy();
        Task SetCompatibilityStrategy(CompatibilityStrategy strategy);
        Task SetSelectorStrategy(VersionSelectorStrategy strategy);
        Task SetCompatibilityStrategy(GrainInterfaceType interfaceType, CompatibilityStrategy strategy);
        Task SetSelectorStrategy(GrainInterfaceType interfaceType, VersionSelectorStrategy strategy);
    }

    internal class VersionStoreGrainState
    {
        internal Dictionary<GrainInterfaceType, CompatibilityStrategy> CompatibilityStrategies { get; }
        internal Dictionary<GrainInterfaceType, VersionSelectorStrategy> VersionSelectorStrategies { get; }
        public VersionSelectorStrategy SelectorOverride { get; set; }
        public CompatibilityStrategy CompatibilityOverride { get; set; }

        public VersionStoreGrainState()
        {
            this.CompatibilityStrategies = new Dictionary<GrainInterfaceType, CompatibilityStrategy>();
            this.VersionSelectorStrategies = new Dictionary<GrainInterfaceType, VersionSelectorStrategy>();
        }
    }

    [StorageProvider(ProviderName = ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)]
    internal class VersionStoreGrain : Grain<VersionStoreGrainState>, IVersionStoreGrain
    {
        public async Task SetCompatibilityStrategy(CompatibilityStrategy strategy)
        {
            this.State.CompatibilityOverride = strategy;
            await this.WriteStateAsync();
        }

        public async Task SetSelectorStrategy(VersionSelectorStrategy strategy)
        {
            this.State.SelectorOverride = strategy;
            await this.WriteStateAsync();
        }

        public async Task SetCompatibilityStrategy(GrainInterfaceType ifaceId, CompatibilityStrategy strategy)
        {
            this.State.CompatibilityStrategies[ifaceId] = strategy;
            await this.WriteStateAsync();
        }

        public async Task SetSelectorStrategy(GrainInterfaceType ifaceId, VersionSelectorStrategy strategy)
        {
            this.State.VersionSelectorStrategies[ifaceId] = strategy;
            await this.WriteStateAsync();
        }

        public bool IsEnabled { get; }

        public Task<Dictionary<GrainInterfaceType, CompatibilityStrategy>> GetCompatibilityStrategies()
        {
            return Task.FromResult(this.State.CompatibilityStrategies);
        }

        public Task<Dictionary<GrainInterfaceType, VersionSelectorStrategy>> GetSelectorStrategies()
        {
            return Task.FromResult(this.State.VersionSelectorStrategies);
        }

        public Task<CompatibilityStrategy> GetCompatibilityStrategy()
        {
            return Task.FromResult(this.State.CompatibilityOverride);
        }

        public Task<VersionSelectorStrategy> GetSelectorStrategy()
        {
            return Task.FromResult(this.State.SelectorOverride);
        }
    }
}
