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

        public Task<string> GetHelloWorldUsingCustomService() => testGrainServiceClient.GetHelloWorldUsingCustomService();

        public Task<bool> CallHasStarted() => testGrainServiceClient.HasStarted();

        public Task<bool> CallHasStartedInBackground() => testGrainServiceClient.HasStartedInBackground();

        public Task<bool> CallHasInit() => testGrainServiceClient.HasInit();

        public Task<string> GetServiceConfigProperty() => testGrainServiceClient.GetServiceConfigProperty();

        public Task<string> EchoViaExtension(string what) => testGrainServiceClient.EchoViaExtension(what);
    }

}
