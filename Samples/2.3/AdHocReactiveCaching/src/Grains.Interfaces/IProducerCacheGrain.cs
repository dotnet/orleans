using System.Threading.Tasks;
using Orleans;

namespace Grains
{
    public interface IProducerCacheGrain : IGrainWithStringKey
    {
        Task<int> GetAsync();
    }
}
