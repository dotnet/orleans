using System;
using System.Threading.Tasks;
using Moq;
using Xunit;

namespace Grains.Tests
{
    /// <summary>
    /// Demonstrates how to test a grain with a timer that performs an internal operation.
    /// </summary>
    public class TimerGrainTests
    {
        [Fact]
        public async Task Increments_Value_On_Timer()
        {
            // mock the grain to override methods
            var grain = new Mock<TimerGrain>() { CallBase = true };

            // mock the timer registration method and capture the action
            Func<object, Task> action = null;
            grain.Setup(_ => _.RegisterTimer(It.IsAny<Func<object, Task>>(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)))
                .Callback<Func<object, Task>, object, TimeSpan, TimeSpan>((a, b, c, d) => action = a);

            // simulate activation
            await grain.Object.OnActivateAsync();

            // assert the timer was registered
            Assert.NotNull(action);

            // assert the initial value is zero
            Assert.Equal(0, await grain.Object.GetValueAsync());

            // tick the timer
            await action(null);

            // assert the new value is one
            Assert.Equal(1, await grain.Object.GetValueAsync());
        }
    }
}