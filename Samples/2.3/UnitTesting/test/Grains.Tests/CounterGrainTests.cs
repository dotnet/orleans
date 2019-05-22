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
        /// Demonstrates a basic set-and-get unit test.
        /// </summary>
        [Fact]
        public async Task Increments_Value_And_Returns_On_Get()
        {
            // arrange
            var counter = new CounterGrain.Counter();
            var state = Mock.Of<IPersistentState<CounterGrain.Counter>>(_ => _.State == counter);
            var timers = Mock.Of<ITimerRegistry>();
            var reminders = Mock.Of<IReminderRegistry>();
            var factory = Mock.Of<IGrainFactory>();
            var grain = new CounterGrain(state, timers, reminders, factory);

            // act
            await grain.IncrementAsync();
            var value = await grain.GetValueAsync();

            // assert
            Assert.Equal(1, value);
        }

        /// <summary>
        /// Demonstrates a test over injected state.
        /// </summary>
        [Fact]
        public async Task Incrementing_Affects_State()
        {
            // arrange
            var counter = new CounterGrain.Counter();
            var state = Mock.Of<IPersistentState<CounterGrain.Counter>>(_ => _.State == counter);
            var timers = Mock.Of<ITimerRegistry>();
            var reminders = Mock.Of<IReminderRegistry>();
            var factory = Mock.Of<IGrainFactory>();
            var grain = new CounterGrain(state, timers, reminders, factory);

            // act
            await grain.IncrementAsync();

            // assert
            Assert.Equal(1, counter.Value);
        }

        /// <summary>
        /// Demonstrates a test on storage method calls on injected state.
        /// </summary>
        [Fact]
        public async Task Saves_State_On_Demand()
        {
            // arrange
            var counter = new CounterGrain.Counter();
            var state = Mock.Of<IPersistentState<CounterGrain.Counter>>(_ => _.State == counter);
            var timers = Mock.Of<ITimerRegistry>();
            var reminders = Mock.Of<IReminderRegistry>();
            var factory = Mock.Of<IGrainFactory>();
            var grain = new CounterGrain(state, timers, reminders, factory);

            // act
            await grain.SaveAsync();

            // assert
            Mock.Get(state).Verify(_ => _.WriteStateAsync());
        }

        /// <summary>
        /// Demonstrates a test that validates a timer registration.
        /// </summary>
        [Fact]
        public async Task Registers_Summary_Timer()
        {
            // arrange
            var counter = new CounterGrain.Counter();
            var state = Mock.Of<IPersistentState<CounterGrain.Counter>>(_ => _.State == counter);
            var timers = Mock.Of<ITimerRegistry>();
            var reminders = Mock.Of<IReminderRegistry>();
            var factory = Mock.Of<IGrainFactory>();

            // arrange - for this test we need to mock the grain so we can override some behaviour
            // we must tell moq to call base class methods to ensure normal grain behaviour
            var grain = new Mock<CounterGrain>(state, timers, reminders, factory) { CallBase = true };

            // arrange - mock the timer registraton method
            // the alternative is for the grain to use the injected timer registry instead - either way works
            grain.Setup(_ => _.RegisterTimer(It.IsAny<Func<object, Task>>(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)))
                .Returns(Mock.Of<IDisposable>());

            // act - simulate activation for the registration to happen
            await grain.Object.OnActivateAsync();

            // assert
            grain.VerifyAll();
        }
    }
}