using System.Threading.Tasks;
using Orleans;

namespace Grains
{
    public interface IAggregatorCacheGrain : IGrainWithStringKey
    {
        Task<int> GetAsync();
    }
}
