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
        Task<Dictionary<int, CompatibilityStrategy>> GetCompatibilityStrategies();
        Task<Dictionary<int, VersionSelectorStrategy>> GetSelectorStrategies();
        Task<CompatibilityStrategy> GetCompatibilityStrategy();
        Task<VersionSelectorStrategy> GetSelectorStrategy();
        Task SetCompatibilityStrategy(CompatibilityStrategy strategy);
        Task SetSelectorStrategy(VersionSelectorStrategy strategy);
        Task SetCompatibilityStrategy(int interfaceId, CompatibilityStrategy strategy);
        Task SetSelectorStrategy(int interfaceId, VersionSelectorStrategy strategy);
    }

    internal class VersionStoreGrainState
    {
        internal Dictionary<int, CompatibilityStrategy> CompatibilityStrategies { get; }
        internal Dictionary<int, VersionSelectorStrategy> VersionSelectorStrategies { get; }
        public VersionSelectorStrategy SelectorOverride { get; set; }
        public CompatibilityStrategy CompatibilityOverride { get; set; }

        public VersionStoreGrainState()
        {
            this.CompatibilityStrategies = new Dictionary<int, CompatibilityStrategy>();
            this.VersionSelectorStrategies = new Dictionary<int, VersionSelectorStrategy>();
        }
    }

    [StorageProvider(ProviderName = Constants.DEFAULT_STORAGE_PROVIDER_NAME)]
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

        public async Task SetCompatibilityStrategy(int ifaceId, CompatibilityStrategy strategy)
        {
            this.State.CompatibilityStrategies[ifaceId] = strategy;
            await this.WriteStateAsync();
        }

        public async Task SetSelectorStrategy(int ifaceId, VersionSelectorStrategy strategy)
        {
            this.State.VersionSelectorStrategies[ifaceId] = strategy;
            await this.WriteStateAsync();
        }

        public bool IsEnabled { get; }

        public Task<Dictionary<int, CompatibilityStrategy>> GetCompatibilityStrategies()
        {
            return Task.FromResult(this.State.CompatibilityStrategies);
        }

        public Task<Dictionary<int, VersionSelectorStrategy>> GetSelectorStrategies()
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
