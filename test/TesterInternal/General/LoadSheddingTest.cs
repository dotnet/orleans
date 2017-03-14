using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.General
{
    // if we parallelize tests, each test should run in isolation 
    public class LoadSheddingTest : OrleansTestingBase, IClassFixture<LoadSheddingTest.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(1);
                return new TestCluster(options);
            }
        }
        public LoadSheddingTest(Fixture fixture)
        {
            this.fixture = fixture;
            HostedCluster = fixture.HostedCluster;
        }

        public TestCluster HostedCluster { get; }

        [Fact, TestCategory("Functional"), TestCategory("LoadShedding")]
        public async Task LoadSheddingBasic()
        {
            ISimpleGrain grain = this.fixture.GrainFactory.GetGrain<ISimpleGrain>(random.Next(), SimpleGrain.SimpleGrainNamePrefix);

            var latchPeriod = TimeSpan.FromSeconds(1);
            await this.HostedCluster.Primary.TestHook.LatchIsOverloaded(true, latchPeriod);

            // Do not accept message in overloaded state
            await Assert.ThrowsAsync<GatewayTooBusyException>(() =>
                grain.SetA(5));
            await Task.Delay(latchPeriod.Multiply(1.1)); // wait for latch to reset
        }

        [Fact, TestCategory("Functional"), TestCategory("LoadShedding")]
        public async Task LoadSheddingComplex()
        {
            ISimpleGrain grain = this.fixture.GrainFactory.GetGrain<ISimpleGrain>(random.Next(), SimpleGrain.SimpleGrainNamePrefix);

            logger.Info("Acquired grain reference");

            await grain.SetA(1);
            logger.Info("First set succeeded");

            var latchPeriod = TimeSpan.FromSeconds(1);
            await this.HostedCluster.Primary.TestHook.LatchIsOverloaded(true, latchPeriod);

            // Do not accept message in overloaded state
            await Assert.ThrowsAsync<GatewayTooBusyException>(() =>
                grain.SetA(2));

            await Task.Delay(latchPeriod.Multiply(1.1)); // wait for latch to reset

            logger.Info("Second set was shed");

            await this.HostedCluster.Primary.TestHook.LatchIsOverloaded(false, latchPeriod);

            // Simple request after overload is cleared should succeed
            await grain.SetA(4);
            logger.Info("Third set succeeded");
            await Task.Delay(latchPeriod.Multiply(1.1)); // wait for latch to reset
        }
    }
}
