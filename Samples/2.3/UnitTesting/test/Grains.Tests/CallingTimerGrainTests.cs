using System;
using System.Threading.Tasks;
using Moq;
using Orleans;
using Xunit;

namespace Grains.Tests
{
    public class CallingTimerGrainTests
    {
        [Fact]
        public async Task Publishes_Counter_To_Summary_On_Timer()
        {
            // arrange the summary grain mock
            var summary = Mock.Of<ISummaryGrain>();

            // arrange the the grain factory mock
            var factory = Mock.Of<IGrainFactory>(_ => _.GetGrain<ISummaryGrain>(Guid.Empty, null) == summary);

            // arrange the grain class mock
            var grain = new Mock<CallingTimerGrain>() { CallBase = true };

            // arrange the grain class factory override
            grain.Setup(_ => _.GrainFactory).Returns(factory);

            // arrange the grain reminder method override
            Func<object, Task> action = null;
            grain.Setup(_ => _.RegisterTimer(It.IsAny<Func<object, Task>>(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)))
                .Callback<Func<object, Task>, object, TimeSpan, TimeSpan>((a, b, c, d) => { action = a; });

            // act by simulating activation
            await grain.Object.OnActivateAsync();

            // assert the timer was registered
            Assert.NotNull(action);

            // act by ticking the timer
            await action(null);

            // assert the summary got the first result
            Mock.Get(summary).Verify(_ => _.SetAsync(nameof(CallingTimerGrain), 0));

            // act by incrementing the value
            await grain.Object.IncrementAsync();

            // act by ticking the timer again
            await action(null);

            // assert the summary got the next result
            Mock.Get(summary).Verify(_ => _.SetAsync(nameof(CallingTimerGrain), 1));
        }
    }
}