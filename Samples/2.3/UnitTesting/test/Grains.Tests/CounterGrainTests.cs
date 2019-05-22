using System;
using System.Threading.Tasks;
using Moq;
using Orleans;
using Orleans.Runtime;
using Orleans.Timers;
using Xunit;

namespace Grains.Tests
{
    public class IsolatedCounterGrainTests
    {
        /// <summary>
        /// Demonstrates how to inject mocked Orleans services via DI.
        /// This is an alternative to overriding the grain methods.
        /// The rest of this test class uses the method overriding approach as not to add redundant tests.
        /// Developers can choose either approach.
        /// </summary>
        [Fact]
        public async Task Mocked_Services_Injection()
        {
            // arrange a state object
            var counter = new CounterGrain.Counter();

            // arrange an injected state service mock
            var state = Mock.Of<IPersistentState<CounterGrain.Counter>>(_ => _.State == counter);

            // arrange a timer registry service mock
            var timers = Mock.Of<ITimerRegistry>();

            // arrange a reminder registry service mock
            var reminders = Mock.Of<IReminderRegistry>();

            // arrange a hosted grain factory service mock
            var factory = Mock.Of<IGrainFactory>();

            // arrange - create a new grain class via mocking to allow overriding of methods
            var grain = new Mock<CounterGrain>(state, timers, reminders, factory) { CallBase = true };

            // act - call a method
            await grain.Object.IncrementAsync();

            // act - call some other method
            var value = await grain.Object.GetValueAsync();

            // assert
            Assert.Equal(1, value);
        }

        /// <summary>
        /// Demonstrates a test for a grain that calls another grain.
        /// </summary>
        [Fact]
        public async Task Publishes_On_Demand()
        {
            // arrange mocks
            var counter = Mock.Of<IPersistentState<CounterGrain.Counter>>(_ => _.State.Value == 123);
            var summary = Mock.Of<ISummaryGrain>();
            var factory = Mock.Of<IGrainFactory>(_ => _.GetGrain<ISummaryGrain>(Guid.Empty, default) == summary);

            // arrange - for this test we need to mock the grain so we can override some behaviour
            // we must tell moq to call base class methods to ensure normal grain behaviour
            var grain = new Mock<CounterGrain>(counter, null, null, null) { CallBase = true };

            // arrange - mock the grain key
            grain.Setup(_ => _.GrainKey).Returns("MyCounter");

            // arrange - mock the grain factory
            grain.Setup(_ => _.GrainFactory).Returns(factory);

            // act
            await grain.Object.PublishAsync();

            // assert
            Mock.Get(summary).Verify(_ => _.SetAsync("MyCounter", 123));
        }
    }
}