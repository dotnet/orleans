using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;

namespace UnitTests
{
    public class AgentTests : OrleansTestingBase, IClassFixture<AgentTests.Fixture>
    {
        private static readonly TimeSpan timeout = TimeSpan.FromSeconds(5);
        private readonly Fixture fixture;

        public AgentTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<Configurator>();
            }

            private class Configurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder.ConfigureServices(services => services.TryAddSingleton<TestDedicatedAsynchAgent>());
                    hostBuilder.ConfigureServices(services => services.TryAddSingleton(sp => sp.GetService(typeof(TestDedicatedAsynchAgent)) as ILifecycleParticipant<ISiloLifecycle>));
                }
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task DedicatedAsynchAgentRestartsTest()
        {
            IAgentTestGrain grain = this.fixture.GrainFactory.GetGrain<IAgentTestGrain>(GetRandomGrainId());
            await TestingUtils.WaitUntilAsync(lastTry => CheckForFailures(grain, lastTry), timeout);
        }

        private async Task<bool> CheckForFailures(IAgentTestGrain grain, bool assertIsTrue)
        {
            int result = await grain.GetFailureCount();
            if (assertIsTrue)
            {
                Assert.True(result > 1);
                return true;
            }
            else
            {
                return result > 1;
            }
        }
    }
}
