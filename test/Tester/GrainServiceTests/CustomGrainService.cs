using System;
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

        public Task<bool> HasStarted()
        {
            return GrainService.HasStarted();
        }

        public Task<bool> HasStartedInBackground()
        {
            return GrainService.HasStartedInBackground();
        }
    }

    public class CustomGrainService : GrainService, ICustomGrainService
    {
        public CustomGrainService(IGrainIdentity id, Silo silo, IGrainServiceConfiguration config) : base(id, silo, config)
        {
            
        }

        private bool m_Started = false;
        private bool m_StartedInBackground = false;

        public override Task Start()
        {
            m_Started = true;
            return base.Start();
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
            m_StartedInBackground = true;
            return TaskDone.Done;
        }

        public Task<bool> HasStarted()
        {
            return Task.FromResult(m_Started);
        }

        public Task<bool> HasStartedInBackground()
        {
            return Task.FromResult(m_StartedInBackground);
        }
    }
}