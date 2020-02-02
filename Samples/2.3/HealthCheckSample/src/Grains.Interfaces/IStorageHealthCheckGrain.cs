using System.Threading.Tasks;
using Orleans;

namespace Grains
{
    public interface IStorageHealthCheckGrain : IGrainWithGuidKey
    {
        Task CheckAsync();
    }
}
