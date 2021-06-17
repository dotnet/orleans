using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IClientAddressableTestProducer : IGrainObserver
    {
        Task<int> Poll();
    }
}
