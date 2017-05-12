using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Core;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Providers;
using Orleans.LogConsistency;
using Orleans.Storage;
using Orleans.Runtime.Storage;

namespace Orleans.Runtime.LogConsistency
{
    internal class LogConsistencyProviderManager : ProviderManagerBase<ILogConsistencyProvider>, ILogConsistencyProviderManager, ILogConsistencyProviderRuntime
    {
        public LogConsistencyProviderManager(IGrainFactory grainFactory, IServiceProvider serviceProvider, IProviderRuntime providerRuntime)
            : base(grainFactory, serviceProvider, providerRuntime)
        {
        }

        public override string ProviderCategory
        {
            get
            {
                return ProviderCategoryConfiguration.LOG_CONSISTENCY_PROVIDER_CATEGORY_NAME;
            }
        }
    }

}