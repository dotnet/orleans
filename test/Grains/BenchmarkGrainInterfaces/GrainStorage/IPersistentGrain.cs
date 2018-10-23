using System.Threading.Tasks;
using Orleans;

namespace BenchmarkGrainInterfaces.GrainStorage
{
    public interface IPersistentGrain : IGrainWithGuidKey
    {
        Task<int> Set(int value);
    }
}
