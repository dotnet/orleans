using System.Collections.Generic;
using Orleans.Providers;
using Orleans.Runtime;

namespace Orleans.LogConsistency
{
    internal interface ILogConsistencyProviderManager : IProviderManager
    {
        IEnumerable<string> GetProviderNames();

        int GetLoadedProvidersNum();

        ILogConsistencyProvider GetDefaultProvider();
        
        bool TryGetProvider(string name, out ILogConsistencyProvider provider, bool caseInsensitive = false);
    }


}
