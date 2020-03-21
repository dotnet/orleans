using System;
using System.Threading.Tasks;
using Grains.Tests.Hosted.Cluster;
using Xunit;

namespace Grains.Tests.Hosted
{
    /// <summary>
    /// Demonstrates testing a grain that calls another grain on demand.
    /// </summary>
    [Collection(nameof(ClusterCollection))]
    public class CallingGrainTests
    {
        private readonly ClusterFixture fixture;

        public CallingGrainTests(ClusterFixture fixture)
        {
            this.fixture = fixture;
        }

        /// <summary>
        /// This demonstrates the integrated testing approach using the global cluster.
        /// </summary>
        /// <returns></returns>
        [Fact]
        public async Task Publishes_On_Demand()
        {
            // we should use a unique grain key as we are using a shared test cluster
            var key = Guid.NewGuid().ToString();

            // get a new grain from the test host
            var grain = fixture.Cluster.GrainFactory.GetGrain<ICallingGrain>(key);

            // increment the value in the grain
            await grain.IncrementAsync();

            // publish the value to the summary
            await grain.PublishAsync();

            // assert the summary was called as expected
            Assert.Equal(1, await fixture.Cluster.GrainFactory.GetGrain<ISummaryGrain>(Guid.Empty).TryGetAsync(key));
        }
    }
}