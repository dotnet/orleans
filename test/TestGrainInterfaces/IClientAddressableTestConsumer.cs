using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IClientAddressableTestConsumer : IGrainWithIntegerKey
    {
        Task<int> PollProducer();
        Task Setup();
    }
}
