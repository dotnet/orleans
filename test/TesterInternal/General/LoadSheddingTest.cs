using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.General
{
    public class LoadSheddingTest : HostedTestClusterPerTest
    {
        [Fact, TestCategory("Functional"), TestCategory("LoadShedding")]
        public async Task LoadSheddingBasic()
        {
            ISimpleGrain grain = GrainClient.GrainFactory.GetGrain<ISimpleGrain>(random.Next(), SimpleGrain.SimpleGrainNamePrefix);

            this.HostedCluster.Primary.TestHook.LatchIsOverloaded(true);

            // Do not accept message in overloaded state
            await Assert.ThrowsAsync<GatewayTooBusyException>(() =>
                grain.SetA(5));
        }

        [Fact, TestCategory("Functional"), TestCategory("LoadShedding")]
        public async Task LoadSheddingComplex()
        {
            ISimpleGrain grain = GrainClient.GrainFactory.GetGrain<ISimpleGrain>(random.Next(), SimpleGrain.SimpleGrainNamePrefix);

            logger.Info("Acquired grain reference");

            await grain.SetA(1);
            logger.Info("First set succeeded");

            this.HostedCluster.Primary.TestHook.LatchIsOverloaded(true);

            // Do not accept message in overloaded state
            await Assert.ThrowsAsync<GatewayTooBusyException>(() =>
                grain.SetA(2));

            logger.Info("Second set was shed");

            this.HostedCluster.Primary.TestHook.LatchIsOverloaded(false);

            // Simple request after overload is cleared should succeed
            await grain.SetA(4);
            logger.Info("Third set succeeded");
        }
    }
}
