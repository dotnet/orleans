using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IGrainServiceTestGrain : IGrainWithIntegerKey
    {
        Task<string> GetHelloWorldUsingCustomService();
        Task<string> GetServiceConfigProperty(string propertyName);
    }
}
