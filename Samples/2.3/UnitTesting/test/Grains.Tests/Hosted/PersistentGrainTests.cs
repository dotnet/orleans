using System;
using System.Linq;
using System.Threading.Tasks;
using Grains.Tests.Hosted.Cluster;
using Orleans;
using Xunit;

namespace Grains.Tests.Hosted
{
    /// <summary>
    /// Demonstrates how to test a grain that persists state to storage.
    /// </summary>
    [Collection(nameof(ClusterCollection))]
    public class PersistentGrainTests
    {
        private readonly ClusterFixture fixture;

        public PersistentGrainTests(ClusterFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public async Task Saves_State()
        {
            // get a brand new grain to test
            var grain = fixture.Cluster.GrainFactory.GetGrain<IPersistentGrain>(Guid.NewGuid());

            // set its value to something we can check
            await grain.SetValueAsync(123);

            // tell it to persist its state
            await grain.SaveAsync();

            // assert that state was saved by one of the silos
            var state = fixture.GetGrainState(typeof(PersistentGrain), "State", grain);
            Assert.NotNull(state);

            // assert that state is of the corect type
            var obj = state.State as PersistentGrain.MyState;
            Assert.NotNull(obj);

            // assert that state has the correct value
            Assert.Equal(123, obj.Value);
        }
    }
}