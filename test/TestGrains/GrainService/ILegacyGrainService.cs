using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Services;

namespace Tester
{
    public interface ILegacyGrainService : IGrainService
    {
        Task<string> GetHelloWorldUsingCustomService(GrainReference reference);
        Task<string> GetServiceConfigProperty(string propertyName);

        Task<bool> HasStarted();
        Task<bool> HasStartedInBackground();
        Task<bool> HasInit();
    }

    public interface ILegacyGrainServiceClient : IGrainServiceClient<ILegacyGrainService>
    {
        Task<string> GetHelloWorldUsingCustomService();
        Task<string> GetServiceConfigProperty(string propertyName);
        Task<bool> HasStarted();
        Task<bool> HasStartedInBackground();
        Task<bool> HasInit();
    }
}