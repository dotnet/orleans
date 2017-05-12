using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Storage
{
    interface IProviderManagerBase<TProvider> : Providers.IProviderManager
    {
        Logger GetLogger(string loggerName);

        IEnumerable<string> GetProviderNames();

        int GetNumLoadedProviders();

        TProvider GetDefaultProvider();

        bool TryGetProvider(string name, out TProvider provider, bool caseInsensitive = false);

        string ProviderCategory { get; }
    }
}
