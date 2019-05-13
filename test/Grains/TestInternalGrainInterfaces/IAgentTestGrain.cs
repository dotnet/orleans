using Orleans;
using System.Threading.Tasks;

namespace UnitTests.GrainInterfaces
{
    public interface IAgentTestGrain : IGrainWithIntegerKey
    {
        Task<int> GetFailureCount();
    }
}
