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

        public Task<string> GetServiceConfigProperty()
        {
            return this.testGrainServiceClient.GetServiceConfigProperty();
        }

        public Task<string> EchoViaExtension(string what)
        {
            return this.testGrainServiceClient.EchoViaExtension(what);
        }
    }

}
