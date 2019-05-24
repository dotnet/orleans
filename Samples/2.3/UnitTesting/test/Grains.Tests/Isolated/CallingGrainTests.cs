using System;
using System.Threading.Tasks;
using Moq;
using Orleans;
using Xunit;

namespace Grains.Tests.Isolated
{
    /// <summary>
    /// Demonstrates testing a grain that calls another grain on demand.
    /// </summary>
    public class CallingGrainTests
    {
        [Fact]
        public async Task Publishes_On_Demand()
        {
            // mock a summary grain
            var summary = Mock.Of<ISummaryGrain>();

            // mock the grain factory
            var factory = Mock.Of<IGrainFactory>(_ => _.GetGrain<ISummaryGrain>(Guid.Empty, null) == summary);

            // mock the grain type so we can mock the base orleans methods
            var grain = new Mock<CallingGrain>() { CallBase = true };
            grain.Setup(_ => _.GrainFactory).Returns(factory);
            grain.Setup(_ => _.GrainKey).Returns("MyCounter");

            // increment the value in the grain
            await grain.Object.IncrementAsync();

            // publish the value to the summary
            await grain.Object.PublishAsync();

            // assert the summary was called as expected
            Mock.Get(summary).Verify(_ => _.SetAsync("MyCounter", 1));
        }
    }
}