using System.Collections.Generic;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.LogConsistency
{
    internal interface ILogConsistencyProviderManager : IProviderManagerBase<ILogConsistencyProvider>
    {
    }
}
