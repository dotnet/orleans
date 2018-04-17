using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IGrainServiceTestGrain : IGrainWithIntegerKey
    {

        Task<string> GetHelloWorldUsingCustomService();
        Task<bool> CallHasStarted();
        Task<bool> CallHasStartedInBackground();
        Task<bool> CallHasInit();
        Task<string> GetServiceConfigProperty();

        Task<string> GetHelloWorldUsingCustomService_Legacy();
        Task<string> GetServiceConfigProperty_Legacy(string propertyName);
        Task<bool> CallHasStarted_Legacy();
        Task<bool> CallHasStartedInBackground_Legacy();
        Task<bool> CallHasInit_Legacy();
    }
}
