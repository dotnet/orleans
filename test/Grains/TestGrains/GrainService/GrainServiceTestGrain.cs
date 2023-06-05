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

        public Task<string> GetHelloWorldUsingCustomService()
        {
            return testGrainServiceClient.GetHelloWorldUsingCustomService();
        }

        public Task<bool> CallHasStarted()
        {
            return testGrainServiceClient.HasStarted();
        }

        public Task<bool> CallHasStartedInBackground()
        {
            return testGrainServiceClient.HasStartedInBackground();
        }

        public Task<bool> CallHasInit()
        {
            return testGrainServiceClient.HasInit();
        }

        public Task<string> GetServiceConfigProperty()
        {
            return testGrainServiceClient.GetServiceConfigProperty();
        }

        public Task<string> EchoViaExtension(string what)
        {
            return testGrainServiceClient.EchoViaExtension(what);
        }
    }

}
