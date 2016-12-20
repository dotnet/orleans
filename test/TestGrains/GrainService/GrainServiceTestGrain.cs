using System.Threading.Tasks;
using Orleans;
using Tester;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class GrainServiceTestGrain : Grain, IGrainServiceTestGrain
    {
        private readonly ICustomGrainServiceClient customGrainServiceClient;

        public Task<string> GetHelloWorldUsingCustomService()
        {
            return this.customGrainServiceClient.GetHelloWorldUsingCustomService();
        }

        public GrainServiceTestGrain(ICustomGrainServiceClient customGrainServiceClient)
        {
            this.customGrainServiceClient = customGrainServiceClient;
        }
    }

}
