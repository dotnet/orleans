using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Orleans.Hosting;

namespace Tester
{
    public class GrainServiceTests : OrleansTestingBase, IClassFixture<GrainServiceTests.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 1;
                builder.AddSiloBuilderConfigurator<GrainSiloBuilderConfigurator>();
                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    legacy.ClusterConfiguration.Globals.RegisterGrainService("CustomGrainService",
                        "Tester.CustomGrainService, Tester",
                        new Dictionary<string, string> {{"test-property", "xyz"}});
                });
            }

            private class GrainSiloBuilderConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder.ConfigureServices(services =>
                        services.AddSingleton<ICustomGrainServiceClient, CustomGrainServiceClient>());
                }
            }
        }

        public GrainServiceTests(Fixture fixture)
        {
            this.GrainFactory = fixture.GrainFactory;
        }

        public IGrainFactory GrainFactory { get; set; }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("GrainServices")]
        public async Task SimpleInvokeGrainService()
        {
            IGrainServiceTestGrain grain = this.GrainFactory.GetGrain<IGrainServiceTestGrain>(0);
            var grainId = await grain.GetHelloWorldUsingCustomService();
            Assert.Equal("Hello World from Grain Service", grainId);
            var prop = await grain.GetServiceConfigProperty("test-property");
            Assert.Equal("xyz", prop);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("GrainServices")]
        public async Task GrainServiceWasStarted()
        {
            IGrainServiceTestGrain grain = GrainFactory.GetGrain<IGrainServiceTestGrain>(0);
            var prop = await grain.CallHasStarted();
            Assert.True(prop);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("GrainServices")]
        public async Task GrainServiceWasStartedInBackground()
        {
            IGrainServiceTestGrain grain = GrainFactory.GetGrain<IGrainServiceTestGrain>(0);
            var prop = await grain.CallHasStartedInBackground();
            Assert.True(prop);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("GrainServices")]
        public async Task GrainServiceWasInit()
        {
            IGrainServiceTestGrain grain = GrainFactory.GetGrain<IGrainServiceTestGrain>(0);
            var prop = await grain.CallHasInit();
            Assert.True(prop);
        }
    }
}