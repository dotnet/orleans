using Orleans.Runtime;
using Orleans.Services;

namespace Tester
{
    public interface ITestGrainService : IGrainService
    {
        Task<string> GetHelloWorldUsingCustomService(GrainReference reference);
        Task<bool> HasStarted();
        Task<bool> HasStartedInBackground();
        Task<bool> HasInit();
        Task<string> GetServiceConfigProperty();
    }

    public interface ITestGrainServiceClient : IGrainServiceClient<ITestGrainService>
    {
        Task<string> GetHelloWorldUsingCustomService();
        Task<bool> HasStarted();
        Task<bool> HasStartedInBackground();
        Task<bool> HasInit();
        Task<string> GetServiceConfigProperty();
        Task<string> EchoViaExtension(string what);
    }
}