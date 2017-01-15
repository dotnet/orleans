using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IClientAddressableTestProducer : IGrainWithIntegerKey
    {
        Task<int> Poll();
    }
}
