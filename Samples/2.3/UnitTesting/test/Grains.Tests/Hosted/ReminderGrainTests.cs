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
    /// Demonstrates how to test a grain with a reminder that performs an internal operation.
    /// </summary>
    [Collection(nameof(ClusterCollection))]
    public class ReminderGrainTests
    {
        private readonly ClusterFixture fixture;

        public ReminderGrainTests(ClusterFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public async Task Increments_Value_On_Reminder()
        {
            // get a new grain to test
            var grain = fixture.Cluster.GrainFactory.GetGrain<IReminderGrain>(Guid.NewGuid());

            // assert the initial value is zero - this will also activate the grain
            Assert.Equal(0, await grain.GetValueAsync());

            // assert the reminder was registered on one of the fake registries
            var reminder = fixture.GetReminder(grain, "IncrementAsync");
            Assert.NotNull(reminder);

            // tick the reminder
            await grain.AsReference<IRemindable>().ReceiveReminder("IncrementAsync", new TickStatus());

            // assert the new value is one
            Assert.Equal(1, await grain.GetValueAsync());
        }
    }
}