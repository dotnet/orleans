using System;
using System.Threading.Tasks;
using Moq;
using Orleans;
using Xunit;

namespace Grains.Tests.Isolated
{
    /// <summary>
    /// Demonstrates how to test a grain that calls another grain upon a timer tick.
    /// </summary>
    public class CallingTimerGrainTests
    {
        [Fact]
        public async Task Publishes_Counter_To_Summary_On_Timer()
        {
            // mock the summary grain
            var summary = Mock.Of<ISummaryGrain>();

            // mock the grain factory and summary grain
            var factory = Mock.Of<IGrainFactory>(_ => _.GetGrain<ISummaryGrain>(Guid.Empty, null) == summary);

            // mock the grain under test and override affected orleans methods
            var grain = new Mock<CallingTimerGrain>() { CallBase = true };
            grain.Setup(_ => _.GrainFactory).Returns(factory);
            grain.Setup(_ => _.GrainKey).Returns("MyGrainKey");

            Func<object, Task> action = null;
            grain.Setup(_ => _.RegisterTimer(It.IsAny<Func<object, Task>>(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)))
                .Callback<Func<object, Task>, object, TimeSpan, TimeSpan>((a, b, c, d) => { action = a; });

            // increment the value while simulating activation
            await grain.Object.OnActivateAsync();
            await grain.Object.IncrementAsync();

            // assert the timer was registered
            Assert.NotNull(action);

            // tick the timer
            await action(null);

            // assert the summary got the first result
            Mock.Get(factory.GetGrain<ISummaryGrain>(Guid.Empty)).Verify(_ => _.SetAsync("MyGrainKey", 1));

            // increment the value
            await grain.Object.IncrementAsync();

            // tick the timer again
            await action(null);

            // assert the summary got the next result
            Mock.Get(summary).Verify(_ => _.SetAsync("MyGrainKey", 2));
        }
    }
}