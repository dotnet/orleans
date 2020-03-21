using Orleans;
using System.Threading.Tasks;

namespace GrainInterfaces
{
    public interface IPingGrain : IGrainWithGuidKey
    {
        Task<int> Ping();

        Task<string> GetRuntimeIdentity();
    }
}
