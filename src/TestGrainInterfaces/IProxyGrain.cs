using System.Threading.Tasks;
using Orleans;

namespace TestInternalGrainInterfaces
{
    public interface IProxyGrain : IGrainWithIntegerKey
    {
        Task CreateProxy(long key);

        Task<string> GetRuntimeInstanceId();

        Task<string> GetProxyRuntimeInstanceId();
    }
}
