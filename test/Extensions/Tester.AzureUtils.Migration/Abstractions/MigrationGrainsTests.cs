using Orleans;
using Orleans.Runtime;
using Orleans.Storage;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;


#if NET7_0_OR_GREATER
using Orleans.Persistence.Migration;
#endif

namespace Tester.AzureUtils.Migration.Abstractions
{
    public abstract class MigrationGrainsTests : MigrationBaseTests
    {
        const int baseId = 200;

        protected MigrationGrainsTests(BaseAzureTestClusterFixture fixture)
            : base(fixture)
        {
        }

        [Fact]
        public async Task ReadFromSourceTest()
        {
            var grain = this.fixture.Client.GetGrain<ISimplePersistentGrain>(baseId + 1);
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
            var grain = this.fixture.Client.GetGrain<ISimplePersistentGrain>(baseId + 2);
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
            var grain = this.fixture.Client.GetGrain<ISimplePersistentGrain>(baseId + 3);
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
            var grain = this.fixture.Client.GetGrain<ISimplePersistentGrain>(baseId + 4);
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
        public async Task DataMigrator_SampleRun()
        {
            var originalEntries = await GenerateGrainsAndSaveAsync(n: 5);

            var stats = await DataMigrator.MigrateGrainsAsync(CancellationToken.None);
            Assert.True(stats.MigratedEntries >= 5);
            Assert.Equal((uint)0, stats.SkippedEntries);
            Assert.Equal((uint)0, stats.FailedEntries);

            var stats2 = await DataMigrator.MigrateGrainsAsync(CancellationToken.None);
            Assert.Equal((uint)0, stats2.MigratedEntries);
            Assert.True(stats2.SkippedEntries >= 5);
            Assert.Equal((uint)0, stats2.FailedEntries);

            // ensure all of the source storage entries have a metadata of "migrationTime" on them
            // in debug purposes and for future reruns
            var currentTime = DateTime.UtcNow;

            var entries = this.SourceExtendedStorage?.GetAll(CancellationToken.None);
            if (entries is null)
            {
                Assert.True(false, "SourceStorageEntriesController is null");
                return;
            }
        }
#endif

        [Fact]
        public async Task GetAll_ReturnsAllListedReferences()
        {
            var originalEntries = await GenerateGrainsAndSaveAsync();

            // iterate over all entries in the storage
            var storageEntries = new Dictionary<Guid, StorageEntry>();
            var entries = this.SourceExtendedStorage?.GetAll(CancellationToken.None);
            if (entries is null)
            {
                Assert.True(false, "SourceStorageEntriesController is null");
                return;
            }

            await foreach (var storageEntry in entries)
            {
                storageEntries.Add(storageEntry.GrainReference.GrainIdentity.PrimaryKey, storageEntry);
            }

            foreach (var originalEntry in originalEntries)
            {
                // checking that every original entry is present in the storage
                // and we are able to access it
                Assert.True(storageEntries.ContainsKey(originalEntry.Key));
            }
        }

        private async Task<IDictionary<Guid, StorageEntryRef>> GenerateGrainsAndSaveAsync(int n = 100)
        {
            var random = new Random();
            var stateName = typeof(SimplePersistentGrain).FullName;

            var storageEntries = new Dictionary<Guid, StorageEntryRef>(n);
            for (var i = 0; i < n; i++)
            {
                var grain = this.fixture.Client.GetGrain<ISimplePersistentGrain>(baseId + i + 50);
                var oldGrainState = new GrainState<SimplePersistentGrain_State>(new() { A = 33, B = 806 });
                var grainReference = (GrainReference)grain;

                try
                {
                    await SourceStorage.WriteStateAsync(stateName, grainReference, oldGrainState);
                } catch (Exception ex) when (ex.Message.Contains("already exists") || ex.InnerException?.Message.Contains("already exists") == true)
                {
                    // we dont care - the grain is already written and that is what matters for the test
                }
                

                storageEntries[grainReference.GrainIdentity.PrimaryKey] = new(grainReference);
            }

            return storageEntries;
        }

        protected struct StorageEntryRef
        {
            public GrainReference GrainReference;

            public StorageEntryRef(GrainReference grainReference)
            {
                GrainReference = grainReference;
            }
        }
    }
}
