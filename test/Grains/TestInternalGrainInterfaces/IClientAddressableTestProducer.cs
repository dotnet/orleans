using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    public interface IClientAddressableTestProducer : IAddressable
    {
        Task<int> Poll();
    }
}
