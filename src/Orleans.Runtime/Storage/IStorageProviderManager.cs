using System.Collections.Generic;
using Orleans.Providers;
using Orleans.Runtime;


namespace Orleans.Storage
{
    internal interface IStorageProviderManager : IProviderManager
    {
        IEnumerable<string> GetProviderNames();

        int GetNumLoadedProviders();

        IStorageProvider GetDefaultProvider();

        bool TryGetProvider(string name, out IStorageProvider provider, bool caseInsensitive = false);
    }
}
