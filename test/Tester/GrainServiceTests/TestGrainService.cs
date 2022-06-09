using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
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
            return GetGrainService(CurrentGrainReference.GrainId).GetHelloWorldUsingCustomService(CurrentGrainReference);
        }

        public Task<bool> HasStarted()
        {
            return GetGrainService(CurrentGrainReference.GrainId).HasStarted();
        }

        public Task<bool> HasStartedInBackground()
        {
            return GetGrainService(CurrentGrainReference.GrainId).HasStartedInBackground();
        }

        public Task<bool> HasInit()
        {
            return GetGrainService(CurrentGrainReference.GrainId).HasInit();
        }

        public Task<string> GetServiceConfigProperty()
        {
            return GetGrainService(CurrentGrainReference.GrainId).GetServiceConfigProperty();
        }

        public Task<string> EchoViaExtension(string what)
        {
            return GetGrainService(CurrentGrainReference.GrainId).AsReference<IEchoExtension>().Echo(what);
        }
    }

    public class TestGrainService : GrainService, ITestGrainService
    {
        private TestGrainServiceOptions config;

        public TestGrainService(GrainId id, Silo silo, ILoggerFactory loggerFactory, IOptions<TestGrainServiceOptions> options) : base(id, silo, loggerFactory)
        {
            this.config = options.Value;
        }

        private bool started = false;
        private bool startedInBackground = false;
        private bool init = false;

        public async override Task Init(IServiceProvider serviceProvider)
        {
            await base.Init(serviceProvider);
            init = true;
        }

        public override Task Start()
        {
            started = true;
            return base.Start();
        }

        public Task<string> GetHelloWorldUsingCustomService(GrainReference reference)
        {
            return Task.FromResult("Hello World from Test Grain Service");
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

        public Task<string> GetServiceConfigProperty()
        {
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
                    .AddOptions<TestGrainServiceOptions>().Configure(o => o.ConfigProperty = configProperty));
        }
    }

    public class TestGrainServiceOptions
    {
        public string ConfigProperty { get; set; }
    }
}