using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

#if NET7_0_OR_GREATER
using Orleans.Persistence.Migration;
#endif

namespace Tester.AzureUtils.Migration.Abstractions
{
    public abstract class MigrationGrainsBaseTests : MigrationBaseTests
    {
        protected MigrationGrainsBaseTests(BaseAzureTestClusterFixture fixture)
            : base(fixture)
        {
        }

        [Fact]
        public async Task ReadFromSourceTest()
        {
            var grain = this.fixture.Client.GetGrain<ISimplePersistentGrain>(100);
            var grainState = new GrainState<SimplePersistentGrain_State>(new() { A = 33, B = 806 });
            var stateName = typeof(SimplePersistentGrain).FullName;

            // Write directly to source storage
            await SourceStorage.WriteStateAsync(stateName, (GrainReference)grain, grainState);

            Assert.Equal(grainState.State.A, await grain.GetA());
            Assert.Equal(grainState.State.A * grainState.State.B, await grain.GetAxB());
        }

        [Fact]
        public async Task ReadFromTargetTest()
        {
            var grain = this.fixture.Client.GetGrain<ISimplePersistentGrain>(200);
            var oldGrainState = new GrainState<SimplePersistentGrain_State>(new() { A = 33, B = 806 });
            var newGrainState = new GrainState<SimplePersistentGrain_State>(new() { A = 20, B = 30 });
            var stateName = typeof(SimplePersistentGrain).FullName;

            // Write directly to storages
            await SourceStorage.WriteStateAsync(stateName, (GrainReference)grain, oldGrainState);
            await DestinationStorage.WriteStateAsync(stateName, (GrainReference)grain, newGrainState);

            Assert.Equal(newGrainState.State.A, await grain.GetA());
            Assert.Equal(newGrainState.State.A * newGrainState.State.B, await grain.GetAxB());
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

        [Fact]
        public async Task ClearAllTest()
        {
            var grain = this.fixture.Client.GetGrain<ISimplePersistentGrain>(400);
            var oldGrainState = new GrainState<SimplePersistentGrain_State>(new() { A = 33, B = 806 });
            var newGrainState = new GrainState<SimplePersistentGrain_State>(new() { A = 20, B = 30 });
            var stateName = typeof(SimplePersistentGrain).FullName;

            // Write directly to storages
            await SourceStorage.WriteStateAsync(stateName, (GrainReference)grain, oldGrainState);
            await DestinationStorage.WriteStateAsync(stateName, (GrainReference)grain, newGrainState);

            // Clear
            var migratedState = new GrainState<SimplePersistentGrain_State>();
            await MigrationStorage.ReadStateAsync(stateName, (GrainReference)grain, migratedState);
            await MigrationStorage.ClearStateAsync(stateName, (GrainReference)grain, migratedState);

            // Read
            var oldGrainState2 = new GrainState<SimplePersistentGrain_State>();
            var newGrainState2 = new GrainState<SimplePersistentGrain_State>();
            await SourceStorage.ReadStateAsync(stateName, (GrainReference)grain, oldGrainState2);
            await DestinationStorage.ReadStateAsync(stateName, (GrainReference)grain, newGrainState2);
            Assert.False(oldGrainState2.RecordExists);
            Assert.False(newGrainState2.RecordExists);
        }

#if NET7_0_OR_GREATER
        [Fact]
        public async Task OfflineMigrator_SampleRun()
        {
            var originalEntries = await GenerateGrainsAndSaveAsync(n: 5);

            var stats = await OfflineMigrator.MigrateGrainsAsync(CancellationToken.None);
            Assert.Equal(originalEntries.Count, stats.MigratedEntries);
            Assert.Equal(0, stats.SkippedEntries);
            Assert.Equal(0, stats.FailedEntries);

            var stats2 = await OfflineMigrator.MigrateGrainsAsync(CancellationToken.None);
            Assert.Equal(0, stats2.MigratedEntries);
            Assert.Equal(originalEntries.Count, stats2.SkippedEntries);
            Assert.Equal(0, stats2.FailedEntries);
        }
#endif

        [Fact]
        public async Task GetAll_ReturnsAllListedReferences()
        {
            var originalEntries = await GenerateGrainsAndSaveAsync();

            var counter = 0;
            await foreach (var storageEntry in SourceStorage.GetAll(CancellationToken.None))
            {
                counter++;
                Assert.NotNull(storageEntry.GrainReference);
                Assert.True(originalEntries.ContainsKey(storageEntry.GrainReference.GrainIdentity.PrimaryKey));
            }

            Assert.Equal(originalEntries.Count, counter);
        }

        private async Task<IDictionary<Guid, StorageEntryRef>> GenerateGrainsAndSaveAsync(int n = 100)
        {
            var random = new Random();
            var stateName = typeof(SimplePersistentGrain).FullName;

            var storageEntries = new Dictionary<Guid, StorageEntryRef>(n);
            for (var i = 0; i < n; i++)
            {
                var grain = this.fixture.Client.GetGrain<ISimplePersistentGrain>(i);
                var oldGrainState = new GrainState<SimplePersistentGrain_State>(new() { A = 33, B = 806 });
                var grainReference = (GrainReference)grain;
                await SourceStorage.WriteStateAsync(stateName, grainReference, oldGrainState);

                storageEntries[grainReference.GrainIdentity.PrimaryKey] = new(grainReference);
            }

            return storageEntries;
        }

        struct StorageEntryRef
        {
            public GrainReference GrainReference;

            public StorageEntryRef(GrainReference grainReference)
            {
                GrainReference = grainReference;
            }
        }
    }
}
