using System.Collections.Generic;
using Orleans.Providers;
using Orleans.Runtime;

namespace Orleans.LogConsistency
{
    internal interface ILogConsistencyProviderManager : IProviderManager
    {
        Logger GetLogger(string loggerName);

        IEnumerable<string> GetProviderNames();

        int GetLoadedProvidersNum();

        bool TryGetProvider(string name, out ILogConsistencyProvider provider, bool caseInsensitive = false);
    }


}
