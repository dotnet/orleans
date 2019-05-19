using System.Collections.Immutable;
using System.Threading.Tasks;
using Grains.Models;
using Orleans;

namespace Grains
{
    public interface IFasterThreadPoolGrain : IGrainWithGuidKey
    {
        Task StartAsync(int hashBuckets, int memorySizeBits);

        Task StopAsync();

        Task SetAsync(LookupItem item);

        Task SetRangeAsync(ImmutableList<LookupItem> items);

        Task SnapshotAsync();

        Task<LookupItem> TryGetAsync(int key);
    }
}