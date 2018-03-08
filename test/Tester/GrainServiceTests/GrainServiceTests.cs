using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Orleans.Hosting;
using Orleans.Services;

namespace Tester
{
    public class GrainServiceTests : OrleansTestingBase, IClassFixture<GrainServiceTests.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 1;
                builder.AddSiloBuilderConfigurator<GrainServiceSiloBuilderConfigurator>();

                // register LegacyGrainService the legacy way
                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    legacy.ClusterConfiguration.Globals.RegisterGrainService("LegacyGrainService",
                        "Tester.LegacyGrainService, Tester",
                        new Dictionary<string, string> {{"test-property", "xyz"}});
                });
            }

            private class GrainServiceSiloBuilderConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder.ConfigureServices(services =>
                        // register client for LegacyGrainService the legacy way
                        services.AddSingleton<ILegacyGrainServiceClient, LegacyGrainServiceClient>())
                    // register TestGrainService the modern way
                    .AddTestGrainService("abc");
                }
            }
        }

        public GrainServiceTests(Fixture fixture)
        {
            this.GrainFactory = fixture.GrainFactory;
        }

        public IGrainFactory GrainFactory { get; set; }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("GrainServices")]
        public async Task SimpleInvokeGrainService_Legacy()
        {
            IGrainServiceTestGrain grain = this.GrainFactory.GetGrain<IGrainServiceTestGrain>(0);
            var grainId = await grain.GetHelloWorldUsingCustomService_Legacy();
            Assert.Equal("Hello World from Legacy Grain Service", grainId);
            var prop = await grain.GetServiceConfigProperty_Legacy("test-property");
            Assert.Equal("xyz", prop);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("GrainServices")]
        public async Task GrainServiceWasStarted_Legacy()
        {
            IGrainServiceTestGrain grain = GrainFactory.GetGrain<IGrainServiceTestGrain>(0);
            var prop = await grain.CallHasStarted_Legacy();
            Assert.True(prop);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("GrainServices")]
        public async Task GrainServiceWasStartedInBackground_Legacy()
        {
            IGrainServiceTestGrain grain = GrainFactory.GetGrain<IGrainServiceTestGrain>(0);
            var prop = await grain.CallHasStartedInBackground_Legacy();
            Assert.True(prop);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("GrainServices")]
        public async Task GrainServiceWasInit_Legacy()
        {
            IGrainServiceTestGrain grain = GrainFactory.GetGrain<IGrainServiceTestGrain>(0);
            var prop = await grain.CallHasInit_Legacy();
            Assert.True(prop);
        }


        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("GrainServices")]
        public async Task SimpleInvokeGrainService()
        {
            IGrainServiceTestGrain grain = this.GrainFactory.GetGrain<IGrainServiceTestGrain>(0);
            var grainId = await grain.GetHelloWorldUsingCustomService();
            Assert.Equal("Hello World from Test Grain Service", grainId);
            var prop = await grain.GetServiceConfigProperty();
            Assert.Equal("abc", prop);
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