using System.Threading.Tasks;
using Moq;
using Orleans.Runtime;
using Xunit;

namespace Grains.Tests.Isolated
{
    /// <summary>
    /// Demonstrates how to test a grain that persists state to storage.
    /// </summary>
    public class PersistentGrainTests
    {
        [Fact]
        public async Task Saves_State()
        {
            // mock a persistent state item
            var state = Mock.Of<IPersistentState<PersistentGrain.MyState>>(_ => _.State == Mock.Of<PersistentGrain.MyState>());

            // create a new grain - we dont mock the grain here because we do not need to override any base methods
            var grain = new PersistentGrain(state);

            // set a new value
            await grain.SetValueAsync(123);

            // save the state
            await grain.SaveAsync();

            // assert the state was saved
            Mock.Get(state).Verify(_ => _.WriteStateAsync());
            Assert.Equal(123, state.State.Value);
        }
    }
}