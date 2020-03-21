using System;
using System.Linq;
using System.Threading.Tasks;
using Grains.Tests.Hosted.Cluster;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace Grains.Tests.Hosted
{
    /// <summary>
    /// Demonstrates how to test a grain with a timer that performs an internal operation.
    /// </summary>
    [Collection(nameof(ClusterCollection))]
    public class TimerGrainTests
    {
        private readonly ClusterFixture fixture;

        public TimerGrainTests(ClusterFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public async Task Increments_Value_On_Timer()
        {
            // using a random key allows parallel testing on the shared cluster
            var key = Guid.NewGuid();

            // get a new instance of the grain
            var grain = fixture.Cluster.GrainFactory.GetGrain<ITimerGrain>(key);

            // assert the initial state is zero - this will activate the grain and register the timer
            Assert.Equal(0, await grain.GetValueAsync());

            // assert the timer was registered on some silo
            var timer = fixture.GetTimers(grain).SingleOrDefault();

            Assert.NotNull(timer);

            // tick the timer
            await timer.TickAsync();

            // assert the new value is one
            Assert.Equal(1, await grain.GetValueAsync());
        }
    }
}