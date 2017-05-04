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

        public Task<bool> HasInit()
        {
            return GrainService.HasInit();
        }
    }

    public class CustomGrainService : GrainService, ICustomGrainService
    {
        public CustomGrainService(IGrainIdentity id, Silo silo, IGrainServiceConfiguration config) : base(id, silo, config)
        {
            
        }

        private bool started = false;
        private bool startedInBackground = false;
        private bool init = false;

        public override Task Init(IServiceProvider serviceProvider)
        {
            init = true;
            return base.Init(serviceProvider);
        }

        public override Task Start()
        {
            started = true;
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
            startedInBackground = true;
            return Task.CompletedTask;
        }

        public Task<bool> HasStarted()
        {
            return Task.FromResult(started);
        }

        public Task<bool> HasStartedInBackground()
        {
            return Task.FromResult(startedInBackground);
        }

        public Task<bool> HasInit()
        {
            return Task.FromResult(init);
        }
    }
}