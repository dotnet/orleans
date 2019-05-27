using System.Threading.Tasks;
using Orleans;

namespace Grains
{
    public interface IReminderGrain : IGrainWithGuidKey
    {
        Task<int> GetValueAsync();
    }
}