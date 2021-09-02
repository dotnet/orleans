using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Core;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Services;
using UnitTests.GrainInterfaces;

namespace Tester
{
    public class TestGrainServiceClient : GrainServiceClient<ITestGrainService>, ITestGrainServiceClient
    {
        public TestGrainServiceClient(IServiceProvider serviceProvider) : base(serviceProvider)
        {
        }

        public Task<string> GetHelloWorldUsingCustomService()
        {
            return GrainService.GetHelloWorldUsingCustomService(CallingGrainReference);
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

        public Task<string> GetServiceConfigProperty()
        {
            return GrainService.GetServiceConfigProperty();
        }

        public Task<string> EchoViaExtension(string what)
        {
            return GrainService.AsReference<IEchoExtension>().Echo(what);
        }
    }

    public class TestGrainService : GrainService, ITestGrainService
    {
        private readonly IGrainIdentity id;
        private TestGrainServiceOptions config;
        private ILogger<TestGrainService> log;

        public TestGrainService(IGrainIdentity id, Silo silo, ILoggerFactory loggerFactory, IOptions<TestGrainServiceOptions> options) : base(id, silo, loggerFactory)
        {
            this.id = id;
            this.config = options.Value;
            this.log = loggerFactory.CreateLogger<TestGrainService>();
        }

        private bool started = false;
        private bool startedInBackground = false;
        private bool init = false;

        public async override Task Init(IServiceProvider serviceProvider)
        {
            this.log.LogInformation("Calling Init on grain service with id {GrainServiceId}", this.GetPrimaryKeyLong(out _));
            await base.Init(serviceProvider);
            init = true;
        }

        public override Task Start()
        {
            this.log.LogInformation("Calling Start on grain service with id {GrainServiceId}", this.GetPrimaryKeyLong(out _));
            started = true;
            return base.Start();
        }

        public Task<string> GetHelloWorldUsingCustomService(GrainReference reference)
        {
            this.log.LogInformation("Calling GetHelloWorldUsingCustomService on grain service with id {GrainServiceId}", this.GetPrimaryKeyLong(out _));
            return Task.FromResult("Hello World from Test Grain Service");
        }

        protected override Task StartInBackground()
        {
            this.log.LogInformation("Calling StartInBackground on grain service with id {GrainServiceId}", this.GetPrimaryKeyLong(out _));
            startedInBackground = true;
            return Task.CompletedTask;
        }

        public Task<bool> HasStarted()
        {
            this.log.LogInformation("Calling HasStarted on grain service with id {GrainServiceId}", this.GetPrimaryKeyLong(out _));
            return Task.FromResult(started);
        }

        public Task<bool> HasStartedInBackground()
        {
            this.log.LogInformation("Calling HasStartedInBackground on grain service with id {GrainServiceId}", this.GetPrimaryKeyLong(out _));
            return Task.FromResult(startedInBackground);
        }

        public Task<bool> HasInit()
        {
            var id = this.GetPrimaryKeyLong(out _);
            this.log.LogInformation("Calling HasInit on grain service with id {GrainServiceId}", id);
            return Task.FromResult(init);
        }

        public Task<string> GetServiceConfigProperty()
        {
            this.log.LogInformation("Calling GetServiceConfigProperty on grain service with id {GrainServiceId}", this.GetPrimaryKeyLong(out _));
            return Task.FromResult(config.ConfigProperty);
        }
    }

    public static class TestGrainServicesSiloBuilderExtensions
    {
        public static ISiloBuilder AddTestGrainService(this ISiloBuilder builder, string configProperty)
        {
            return builder.AddGrainService<TestGrainService>()
                .ConfigureServices(services => services
                    .AddSingleton<ITestGrainServiceClient, TestGrainServiceClient>()
                    .AddOptions<TestGrainServiceOptions>().Configure( o => o.ConfigProperty = configProperty));
        }
    }

    public class TestGrainServiceOptions
    {
        public string ConfigProperty { get; set; }
    }
}