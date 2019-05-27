using System;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Xunit;

namespace Grains.Tests.Hosted
{
    /// <summary>
    /// Demonstrates how to test a grain that calls another grain upon a timer tick.
    /// </summary>
    [Collection(nameof(ClusterCollection))]
    public class CallingTimerGrainTests
    {
        private readonly ClusterFixture fixture;

        public CallingTimerGrainTests(ClusterFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public async Task Publishes_Counter_To_Summary_On_Timer()
        {
            // using a random key allows parallel testing on the shared cluster
            var key = Guid.NewGuid().ToString();

            // get a new grain instance to test
            var grain = fixture.Cluster.GrainFactory.GetGrain<ICallingTimerGrain>(key);

            // increment the counter - this will also activate the grain and register the timer
            await grain.IncrementAsync();

            // assert the timer was registered on some silo in the cluster
            var timer = fixture.TimerRegistryInstances
                .SelectMany(_ => _.GetAll())
                .Where(_ => _.Grain.AsReference<ICallingTimerGrain>().Equals(grain))
                .SingleOrDefault();

            Assert.NotNull(timer);

            // tick the timer once
            await timer.TickAsync();

            // assert the summary grain got the first result
            var summary = await fixture.Cluster.GrainFactory.GetGrain<ISummaryGrain>(Guid.Empty).TryGetAsync(key);
            Assert.Equal(1, summary);

            // increment the counter again
            await grain.IncrementAsync();

            // tick the timer again
            await timer.TickAsync();

            // assert the summary grain got the second result
            summary = await fixture.Cluster.GrainFactory.GetGrain<ISummaryGrain>(Guid.Empty).TryGetAsync(key);
            Assert.Equal(2, summary);
        }
    }
}