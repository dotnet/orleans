using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    [Factory(FactoryAttribute.FactoryTypes.ClientObject)]
    public interface IClientAddressableTestProducer : IGrainWithIntegerKey
    {
        Task<int> Poll();
    }
}
