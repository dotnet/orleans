using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IClientAddressableTestRendezvousGrain : IGrainWithIntegerKey
    {
        Task<IClientAddressableTestProducer> GetProducer();
        Task SetProducer(IClientAddressableTestProducer producer);
    }
}
