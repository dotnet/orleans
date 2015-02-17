using System.Threading.Tasks;
using Orleans;

namespace UnitTestGrainInterfaces
{
    [Factory(FactoryAttribute.FactoryTypes.Grain)]
    public interface IClientAddressableTestGrain : IGrain
    {
        Task SetTarget(IClientAddressableTestClientObject target);
        Task<string> HappyPath(string message);
        Task SadPath(string message);
        Task MicroSerialStressTest(int iterationCount);
        Task MicroParallelStressTest(int iterationCount);
    }
}
