using System.Threading.Tasks;
using Orleans.TestingHost;
using Tester;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.TestingHost
{
    public class TestingSiloHostTests
    {
        [Fact, TestCategory("SlowBVT"), TestCategory("Functional"), TestCategory("Testing")]
        public async Task AllowClusterReuseBetweenInvocations()
        {
            try
            {
                var host = new TestingSiloHost(startFreshOrleans: true);
                var initialDeploymentId = host.DeploymentId;
                var initialSilo = host.Primary.Silo;
                var grain = host.GrainFactory.GetGrain<ISimpleGrain>(TestUtils.GetRandomGrainId());
                await grain.GetA();

                host = new TestingSiloHost(startFreshOrleans: false);
                Assert.Equal(initialDeploymentId, host.DeploymentId);
                Assert.Same(initialSilo, host.Primary.Silo);
                grain = host.GrainFactory.GetGrain<ISimpleGrain>(TestUtils.GetRandomGrainId());
                await grain.GetA();
            }
            finally
            {
                TestingSiloHost.StopAllSilosIfRunning();
            }
        }
    }
}
