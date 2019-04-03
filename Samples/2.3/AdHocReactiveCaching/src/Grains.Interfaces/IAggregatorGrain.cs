using System.Threading.Tasks;
using Grains.Models;
using Orleans;

namespace Grains
{
    public interface IAggregatorGrain : IGrainWithStringKey
    {
        Task<VersionedValue<int>> GetAsync();
        Task<VersionedValue<int>> LongPollAsync(VersionToken knownVersion);
    }
}
