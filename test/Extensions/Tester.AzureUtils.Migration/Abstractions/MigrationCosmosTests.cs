using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace Tester.AzureUtils.Migration.Abstractions
{
    public abstract class MigrationCosmosTests : MigrationBaseTests
    {
        protected MigrationCosmosTests(BaseAzureTestClusterFixture fixture)
            : base(fixture)
        {
        }

        [Fact]
        public async Task ReadFromSourceThenWriteToTargetTest()
        {
            var grain = this.fixture.Client.GetGrain<ISimplePersistentGrain>(300);
            var oldGrainState = new GrainState<SimplePersistentGrain_State>(new() { A = 33, B = 806 });
            var newState = new SimplePersistentGrain_State { A = 20, B = 30 };
            var stateName = typeof(SimplePersistentGrain).FullName;

            // Write directly to source storage
            await SourceStorage.WriteStateAsync(stateName, (GrainReference)grain, oldGrainState);

            // Grain should read from source but write to destination
            Assert.Equal(oldGrainState.State.A, await grain.GetA());
            Assert.Equal(oldGrainState.State.A * oldGrainState.State.B, await grain.GetAxB());
            await grain.SetA(newState.A);
            await grain.SetB(newState.B);

            var newGrainState = new GrainState<SimplePersistentGrain_State>();
            await DestinationStorage.ReadStateAsync(stateName, (GrainReference)grain, newGrainState);

            Assert.Equal(newGrainState.State.A, await grain.GetA());
            Assert.Equal(newGrainState.State.A * newGrainState.State.B, await grain.GetAxB());
        }
    }
}
