using System.Threading.Tasks;
using Orleans;
using Tester;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class GrainServiceTestGrain : Grain, IGrainServiceTestGrain
    {
        public Task<string> GetHelloWorldUsingCustomService()
        {
            var service = (ICustomGrainServiceClient)this.ServiceProvider.GetService(typeof(ICustomGrainServiceClient));
            return service.GetHelloWorldUsingCustomService();
        }
    }

}
