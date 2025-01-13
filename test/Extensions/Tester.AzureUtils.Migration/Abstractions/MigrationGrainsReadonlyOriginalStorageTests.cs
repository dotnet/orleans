using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;


#if NET7_0_OR_GREATER
using Orleans.Persistence.Migration;
#endif

namespace Tester.AzureUtils.Migration.Abstractions
{
    public abstract class MigrationGrainsReadonlyOriginalStorageTests : MigrationGrainsTests
    {
        protected MigrationGrainsReadonlyOriginalStorageTests(BaseAzureTestClusterFixture fixture)
            : base(fixture)
        {
        }

        [Fact]
        public async Task ReadFromSourceThenWriteToTargetTest_OnlyWritesToDestinationStorage()
        {
            var grain = this.fixture.Client.GetGrain<ISimplePersistentGrain>(1300);
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

            // but original storage should not have an updated state at this point!
            var originalStorageState = new GrainState<SimplePersistentGrain_State>();
            await SourceStorage.ReadStateAsync(stateName, (GrainReference)grain, originalStorageState);

            Assert.Equal(originalStorageState.State.A, oldGrainState.State.A);
            Assert.Equal(originalStorageState.State.B, oldGrainState.State.B);
        }


    }
}
