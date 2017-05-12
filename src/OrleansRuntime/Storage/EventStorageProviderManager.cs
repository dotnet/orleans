using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Storage;

namespace Orleans.Runtime.Storage
{
    internal class EventStorageProviderManager : ProviderManagerBase<IEventStorageProvider>, IEventStorageProviderManager, IEventStorageProviderRuntime
    {
        public EventStorageProviderManager(IGrainFactory grainFactory, IServiceProvider serviceProvider, IProviderRuntime providerRuntime)
            : base(grainFactory, serviceProvider, providerRuntime)
        {
        }

        public override string ProviderCategory
        {
            get
            {
                return ProviderCategoryConfiguration.EVENT_STORAGE_PROVIDER_CATEGORY_NAME;
            }
        }

    }
}
