using System.Collections.Immutable;
using System.Threading.Tasks;
using Grains.Models;
using Orleans;

namespace Grains
{
    public interface IDictionaryLookupGrain : IGrainWithGuidKey
    {
        Task StartAsync();

        Task StopAsync();

        Task SetAsync(LookupItem item);

        Task SetRangeAsync(ImmutableList<LookupItem> items);

        Task<LookupItem> TryGetAsync(int key);
    }
}