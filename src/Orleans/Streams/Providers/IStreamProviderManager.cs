using System.Collections.Generic;
using Orleans.Providers;

namespace Orleans.Streams
{
    public interface IStreamProviderManager : IProviderManager
    {
        IEnumerable<IStreamProvider> GetStreamProviders();
    }
}
