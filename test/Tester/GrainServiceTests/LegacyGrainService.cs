using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Core;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Services;

namespace Tester
{
    public class LegacyGrainServiceClient : GrainServiceClient<ILegacyGrainService>, ILegacyGrainServiceClient
    {
        public LegacyGrainServiceClient(IServiceProvider serviceProvider) : base(serviceProvider)
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

    public class LegacyGrainService : GrainService, ILegacyGrainService
    {
        private readonly IGrainIdentity id;
        private IGrainServiceConfiguration config;

        public LegacyGrainService(IGrainIdentity id, Silo silo, ILoggerFactory loggerFactory) : base(id, silo, loggerFactory)
        {
            this.id = id;
        }

        private bool started = false;
        private bool startedInBackground = false;
        private bool init = false;

        public async override Task Init(IServiceProvider serviceProvider)
        {
            await base.Init(serviceProvider);
            this.config = serviceProvider.GetRequiredServiceByKey<Type, IGrainServiceConfiguration>(this.GetType());
            init = true;
        }

        public override Task Start()
        {
            started = true;
            return base.Start();
        }

        public Task<string> GetHelloWorldUsingCustomService(GrainReference reference)
        {
            return Task.FromResult("Hello World from Legacy Grain Service");
        }

        public Task<string> GetServiceConfigProperty(string propertyName)
        {
            return Task.FromResult(this.config.Properties[propertyName]);
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