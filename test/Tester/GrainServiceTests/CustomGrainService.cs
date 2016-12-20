using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Core;
using Orleans.Runtime;
using Orleans.Runtime.Services;

namespace Tester
{
    public class CustomGrainServiceClient : GrainServiceClient<ICustomGrainService>, ICustomGrainServiceClient
    {
        public CustomGrainServiceClient(IServiceProvider serviceProvider) : base(serviceProvider)
        {
        }

        public Task<string> GetHelloWorldUsingCustomService()
        {
            return GrainService.GetHelloWorldUsingCustomService(CallingGrainReference);
        }
    }

    public class CustomGrainService : GrainService, ICustomGrainService
    {
        public CustomGrainService(IGrainIdentity id, Silo silo) : base(id, silo)
        {
            
        }

        public Task<string> GetHelloWorldUsingCustomService(GrainReference reference)
        {
            return Task.FromResult("Hello World from Grain Service");
        }

        protected override Task StartInBackground()
        {
            return TaskDone.Done;
        }
    }
}