using System.Threading.Tasks;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester.TestingHost
{
    public class TestingSiloHostTests
    {
        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Testing")]
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
