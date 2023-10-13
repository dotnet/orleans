using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    public interface IGrainServiceTestGrain : IGrainWithIntegerKey
    {

        Task<string> GetHelloWorldUsingCustomService();
        Task<bool> CallHasStarted();
        Task<bool> CallHasStartedInBackground();
        Task<bool> CallHasInit();
        Task<string> GetServiceConfigProperty();
        Task<string> EchoViaExtension(string what);
    }

    public interface IEchoExtension : IGrainExtension
    {
        Task<string> Echo(string what);
    }
}
