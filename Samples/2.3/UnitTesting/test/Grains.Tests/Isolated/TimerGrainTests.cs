using System;
using System.Threading.Tasks;
using Moq;
using Xunit;

namespace Grains.Tests.Isolated
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
            object state = null;
            var dueTime = TimeSpan.FromSeconds(1);
            var period = TimeSpan.FromSeconds(1);
            grain.Setup(_ => _.RegisterTimer(It.IsAny<Func<object, Task>>(), It.IsAny<object>(), It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>()))
                .Callback<Func<object, Task>, object, TimeSpan, TimeSpan>((a, b, c, d) => { action = a; state = b; dueTime = c; period = d; })
                .Returns(Mock.Of<IDisposable>());

            // simulate activation
            await grain.Object.OnActivateAsync();

            // assert the timer was registered
            Assert.NotNull(action);
            Assert.Null(state);
            Assert.Equal(TimeSpan.FromSeconds(1), dueTime);
            Assert.Equal(TimeSpan.FromSeconds(1), period);

            // assert the initial value is zero
            Assert.Equal(0, await grain.Object.GetValueAsync());

            // tick the timer
            await action(null);

            // assert the new value is one
            Assert.Equal(1, await grain.Object.GetValueAsync());
        }
    }
}