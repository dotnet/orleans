using System;
using System.Threading.Tasks;
using Moq;
using Orleans.Runtime;
using Xunit;

namespace Grains.Tests.Isolated
{
    /// <summary>
    /// Demonstrates how to test a grain with a reminder that performs an internal operation.
    /// </summary>
    public class ReminderGrainTests
    {
        [Fact]
        public async Task Increments_Value_On_Reminder()
        {
            // mock the grain
            var grain = new Mock<ReminderGrain>() { CallBase = true };
            grain.Setup(_ => _.RegisterOrUpdateReminder("IncrementAsync", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)))
                .Returns(Task.FromResult(Mock.Of<IGrainReminder>(_ => _.ReminderName == "IncrementAsync")));

            // simulate activation
            await grain.Object.OnActivateAsync();

            // ensure the reminder was registered
            grain.Verify(_ => _.RegisterOrUpdateReminder("IncrementAsync", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)));

            // assert the initial value is zero
            Assert.Equal(0, await grain.Object.GetValueAsync());

            // tick the reminder
            await grain.Object.ReceiveReminder("IncrementAsync", new TickStatus());

            // assert the new value is one
            Assert.Equal(1, await grain.Object.GetValueAsync());
        }
    }
}