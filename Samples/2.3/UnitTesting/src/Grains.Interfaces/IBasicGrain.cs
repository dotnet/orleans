using System.Threading.Tasks;
using Orleans;

namespace Grains
{
    public interface IBasicGrain : IGrainWithGuidKey
    {
        Task SetValueAsync(int value);

        Task<int> GetValueAsync();
    }
}