using Xunit;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;

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
            }

            private class GrainServiceSiloBuilderConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder.AddTestGrainService("abc").AddGrainExtension<IEchoExtension, EchoExtension>();
                }
            }
        }

        public GrainServiceTests(Fixture fixture)
        {
            this.GrainFactory = fixture.GrainFactory;
        }

        public IGrainFactory GrainFactory { get; set; }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public async Task SimpleInvokeGrainService()
        {
            IGrainServiceTestGrain grain = this.GrainFactory.GetGrain<IGrainServiceTestGrain>(0);
            var grainId = await grain.GetHelloWorldUsingCustomService();
            Assert.Equal("Hello World from Test Grain Service", grainId);
            var prop = await grain.GetServiceConfigProperty();
            Assert.Equal("abc", prop);
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public async Task GrainServiceWasStarted()
        {
            IGrainServiceTestGrain grain = GrainFactory.GetGrain<IGrainServiceTestGrain>(0);
            var prop = await grain.CallHasStarted();
            Assert.True(prop);
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public async Task GrainServiceWasStartedInBackground()
        {
            IGrainServiceTestGrain grain = GrainFactory.GetGrain<IGrainServiceTestGrain>(0);
            var prop = await grain.CallHasStartedInBackground();
            Assert.True(prop);
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public async Task GrainServiceWasInit()
        {
            IGrainServiceTestGrain grain = GrainFactory.GetGrain<IGrainServiceTestGrain>(0);
            var prop = await grain.CallHasInit();
            Assert.True(prop);
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public async Task GrainServiceExtensionTest()
        {
            IGrainServiceTestGrain grain = GrainFactory.GetGrain<IGrainServiceTestGrain>(0);
            var what = await grain.EchoViaExtension("what");
            Assert.Equal("what", what);
        }

        public class EchoExtension : IEchoExtension
        {
            public Task<string> Echo(string what)
            {
                return Task.FromResult(what);
            }
        }
    }
}