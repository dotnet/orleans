using System.Threading.Tasks;
using Orleans;

namespace Grains
{
    public interface ITimerGrain : IGrainWithGuidKey
    {
        Task<int> GetValueAsync();
    }
}