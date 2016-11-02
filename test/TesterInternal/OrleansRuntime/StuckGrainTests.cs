using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Tester;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.StuckGrainTests
{
    /// <summary>
    /// Summary description for PersistenceTest
    /// </summary>
    public class StuckGrainTests : OrleansTestingBase, IClassFixture<StuckGrainTests.Fixture>
    {
        private class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                GlobalConfiguration.ENFORCE_MINIMUM_REQUIREMENT_FOR_AGE_LIMIT = false;
                var options = new TestClusterOptions(1);
                options.ClusterConfiguration.Globals.Application.SetDefaultCollectionAgeLimit(TimeSpan.FromSeconds(1));
                options.ClusterConfiguration.Globals.CollectionQuantum = TimeSpan.FromSeconds(1);

                return new TestCluster(options);
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivationCollection")]
        public async Task StuckGrainTest_Basic()
        {
            var id = Guid.NewGuid();
            var stuckGrain = GrainClient.GrainFactory.GetGrain<IStuckGrain>(id);
            var task = stuckGrain.RunForever();

            // Should timeout
            await Assert.ThrowsAsync<TimeoutException>(() => task.WithTimeout(TimeSpan.FromSeconds(1)));

            var cleaner = GrainClient.GrainFactory.GetGrain<IStuckCleanGrain>(id);
            await cleaner.Release(id);

            // Should complete now
            await task.WithTimeout(TimeSpan.FromSeconds(1));

            // wait for activation collection
            await Task.Delay(TimeSpan.FromSeconds(5)); 

            Assert.False(await cleaner.IsActivated(id), "Grain activation is supposed be garbage collected, but it is still running.");
        }
    }
}
