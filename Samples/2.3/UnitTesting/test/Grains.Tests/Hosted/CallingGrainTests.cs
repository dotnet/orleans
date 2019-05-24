using System;
using System.Threading.Tasks;
using Orleans.TestingHost;
using Xunit;

namespace Grains.Tests.Hosted
{
    /// <summary>
    /// Demonstrates testing a grain that calls another grain on demand.
    /// </summary>
    [Collection(nameof(ClusterCollection))]
    public class CallingGrainTests
    {
        private readonly TestCluster cluster;

        public CallingGrainTests(ClusterFixture fixture)
        {
            cluster = fixture.Cluster;
        }

        /// <summary>
        /// This demonstrates the integrated testing approach using the global cluster.
        /// </summary>
        /// <returns></returns>
        [Fact]
        public async Task Publishes_On_Demand()
        {
            // get a new grain from the test host
            var grain = cluster.GrainFactory.GetGrain<ICallingGrain>("MyCounter");

            // increment the value in the grain
            await grain.IncrementAsync();

            // publish the value to the summary
            await grain.PublishAsync();

            // assert the summary was called as expected
            var value = await cluster.GrainFactory.GetGrain<ISummaryGrain>(Guid.Empty).TryGetAsync("MyCounter");
            Assert.Equal(1, value);
        }
    }
}