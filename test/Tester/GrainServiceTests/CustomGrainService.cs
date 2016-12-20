using System;
using System.Runtime.Remoting.Channels;
using System.Threading.Tasks;
using Orleans;
using Orleans.Core;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
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

        public Task<string> GetServiceConfigProperty(string propertyName)
        {
            return GrainService.GetServiceConfigProperty(propertyName);
        }
    }

    public class CustomGrainService : GrainService, ICustomGrainService
    {
        public CustomGrainService(IGrainIdentity id, Silo silo, IGrainServiceConfiguration config) : base(id, silo, config)
        {
            
        }

        public Task<string> GetHelloWorldUsingCustomService(GrainReference reference)
        {
            return Task.FromResult("Hello World from Grain Service");
        }

        public Task<string> GetServiceConfigProperty(string propertyName)
        {
            return Task.FromResult(base.Config.Properties[propertyName]);
        }

        protected override Task StartInBackground()
        {
            return TaskDone.Done;
        }
    }
}