using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface ISimpleDIGrain : IGrainWithIntegerKey
    {
        Task<long> GetTicksFromService();
        Task<string> GetStringValue();
    }

    public interface IDIGrainWithInjectedServices : ISimpleDIGrain
    {
        Task<long> GetGrainFactoryId();
    }
}
