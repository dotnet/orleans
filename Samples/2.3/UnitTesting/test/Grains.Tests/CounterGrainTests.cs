using System.Threading.Tasks;
using Moq;
using Orleans.Runtime;
using Xunit;

namespace Grains.Tests
{
    public class IsolatedCounterGrainTests
    {
        [Fact]
        public async Task Increments_Value_And_Returns_On_Get()
        {
            // arrange
            var counter = new CounterGrain.Counter();
            var state = Mock.Of<IPersistentState<CounterGrain.Counter>>(_ => _.State == counter);
            var grain = new CounterGrain(state);

            // act
            await grain.IncrementAsync();
            var value = await grain.GetValueAsync();

            // assert
            Assert.Equal(1, value);
        }

        [Fact]
        public async Task Incrementing_Affects_State()
        {
            // arrange
            var counter = new CounterGrain.Counter();
            var state = Mock.Of<IPersistentState<CounterGrain.Counter>>(_ => _.State == counter);
            var grain = new CounterGrain(state);

            // act
            await grain.IncrementAsync();

            // assert
            Assert.Equal(1, counter.Value);
        }

        [Fact]
        public async Task Saves_State_On_Demand()
        {
            // arrange
            var counter = new CounterGrain.Counter();
            var state = Mock.Of<IPersistentState<CounterGrain.Counter>>(_ => _.State == counter);
            var grain = new CounterGrain(state);

            // act
            await grain.SaveAsync();

            // assert
            Mock.Get(state).Verify(_ => _.WriteStateAsync());
        }
    }
}