using System.Threading.Tasks;
using Orleans;
using Tester;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class GrainServiceTestGrain : Grain, IGrainServiceTestGrain
    {
        private readonly ILegacyGrainServiceClient legacyGrainServiceClient;
        private readonly ITestGrainServiceClient testGrainServiceClient;

        public GrainServiceTestGrain(ILegacyGrainServiceClient legacyGrainServiceClient, ITestGrainServiceClient testGrainServiceClient)
        {
            this.legacyGrainServiceClient = legacyGrainServiceClient;
            this.testGrainServiceClient = testGrainServiceClient;
        }

        public Task<string> GetHelloWorldUsingCustomService()
        {
            return this.testGrainServiceClient.GetHelloWorldUsingCustomService();
        }

        public Task<bool> CallHasStarted()
        {
            return this.testGrainServiceClient.HasStarted();
        }

        public Task<bool> CallHasStartedInBackground()
        {
            return this.testGrainServiceClient.HasStartedInBackground();
        }

        public Task<bool> CallHasInit()
        {
            return this.testGrainServiceClient.HasInit();
        }

        public Task<string> GetHelloWorldUsingCustomService_Legacy()
        {
            return this.legacyGrainServiceClient.GetHelloWorldUsingCustomService();
        }

        public Task<string> GetServiceConfigProperty_Legacy(string propertyName)
        {
            return this.legacyGrainServiceClient.GetServiceConfigProperty(propertyName);
        }

        public Task<bool> CallHasStarted_Legacy()
        {
            return this.legacyGrainServiceClient.HasStarted();
        }

        public Task<bool> CallHasStartedInBackground_Legacy()
        {
            return this.legacyGrainServiceClient.HasStartedInBackground();
        }

        public Task<bool> CallHasInit_Legacy()
        {
            return this.legacyGrainServiceClient.HasInit();
        }

        public Task<string> GetServiceConfigProperty()
        {
            return this.testGrainServiceClient.GetServiceConfigProperty();
        }
    }

}
