using System.Collections.Generic;

using Orleans.Runtime;
using Orleans.Providers;


namespace Orleans.Storage
{
    internal interface IStorageProviderManager : IProviderManager
    {
        Logger GetLogger(string loggerName);

        IEnumerable<string> GetProviderNames();

        int GetNumLoadedProviders();

        IStorageProvider GetDefaultProvider();

        bool TryGetProvider(string name, out IStorageProvider provider, bool caseInsensitive = false);
    }
}
