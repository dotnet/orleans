using System.Threading.Tasks;
using Orleans;
using Tester;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class GrainServiceTestGrain : Grain, IGrainServiceTestGrain
    {
        private readonly ITestGrainServiceClient testGrainServiceClient;

        public GrainServiceTestGrain(ITestGrainServiceClient testGrainServiceClient)
        {
            this.testGrainServiceClient = testGrainServiceClient;
        }

        public Task<string> GetHelloWorldUsingCustomService() => this.testGrainServiceClient.GetHelloWorldUsingCustomService();

        public Task<bool> CallHasStarted() => this.testGrainServiceClient.HasStarted();

        public Task<bool> CallHasStartedInBackground() => this.testGrainServiceClient.HasStartedInBackground();

        public Task<bool> CallHasInit() => this.testGrainServiceClient.HasInit();

        public Task<string> GetServiceConfigProperty() => this.testGrainServiceClient.GetServiceConfigProperty();

        public Task<string> EchoViaExtension(string what) => this.testGrainServiceClient.EchoViaExtension(what);
    }

}
