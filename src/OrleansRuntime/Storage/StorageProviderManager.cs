using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Storage;

namespace Orleans.Runtime.Storage
{
    internal class StorageProviderManager : ProviderManagerBase<IStorageProvider>, IStorageProviderManager, IStorageProviderRuntime
    {
        public StorageProviderManager(IGrainFactory grainFactory, IServiceProvider serviceProvider, IProviderRuntime providerRuntime) 
            : base(grainFactory, serviceProvider, providerRuntime)
        {
        }

        public override string ProviderCategory
        {
            get
            {
                return ProviderCategoryConfiguration.STORAGE_PROVIDER_CATEGORY_NAME;
            }
        }

        public override bool TryGetProvider(string name, out IStorageProvider provider, bool caseInsensitive = false)
        {
            // Look for MemoryStore provider as special case name
            caseInsensitive = caseInsensitive || Constants.MEMORY_STORAGE_PROVIDER_NAME.Equals(name, StringComparison.OrdinalIgnoreCase);

            return base.TryGetProvider(name, out provider, caseInsensitive);
        }

        // used only for testing
        internal Task LoadEmptyStorageProviders()
        {
            return base.LoadEmptyProviders();
        }

        // used only for testing
        internal async Task AddAndInitProvider(string name, IStorageProvider provider, IProviderConfiguration config=null)
        {
            await provider.Init(name, this, config);
            providerLoader.AddProvider(name, provider, config);
        }
    }
}
