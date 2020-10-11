using System.Threading.Tasks;
using Orleans;

namespace Grains
{
    public interface IPersistentGrain : IGrainWithGuidKey
    {
        Task SetValueAsync(int value);
        Task SaveAsync();
    }
}