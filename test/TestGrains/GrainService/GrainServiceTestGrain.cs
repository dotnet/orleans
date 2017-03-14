using System.Threading.Tasks;
using Orleans;
using Tester;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class GrainServiceTestGrain : Grain, IGrainServiceTestGrain
    {
        private readonly ICustomGrainServiceClient customGrainServiceClient;

        public GrainServiceTestGrain(ICustomGrainServiceClient customGrainServiceClient)
        {
            this.customGrainServiceClient = customGrainServiceClient;
        }

        public Task<string> GetHelloWorldUsingCustomService()
        {
            return this.customGrainServiceClient.GetHelloWorldUsingCustomService();
        }

        public Task<string> GetServiceConfigProperty(string propertyName)
        {
            return this.customGrainServiceClient.GetServiceConfigProperty(propertyName);
        }

        public Task<bool> CallHasStarted()
        {
            return this.customGrainServiceClient.HasStarted();
        }

        public Task<bool> CallHasStartedInBackground()
        {
            return this.customGrainServiceClient.HasStartedInBackground();
        }

        public Task<bool> CallHasInit()
        {
            return this.customGrainServiceClient.HasInit();
        }
    }

}
