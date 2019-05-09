using System.Collections.Immutable;
using System.Threading.Tasks;
using Grains.Models;
using Orleans;

namespace Grains
{
    public interface IVolatileLookupGrain : IGrainWithStringKey
    {
        Task StartAsync();

        Task SetAsync(LookupItem item);

        Task SetAsync(ImmutableList<LookupItem> items);

        Task<LookupItem> GetAsync(int key);
    }
}