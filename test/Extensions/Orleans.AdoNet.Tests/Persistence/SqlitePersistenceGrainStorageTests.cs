using System.Globalization;
using Microsoft.Data.Sqlite;
using Orleans.Runtime;
using Orleans.Storage;
using TestExtensions;
using UnitTests.StorageTests.Relational;
using UnitTests.StorageTests.Relational.TestDataSets;
using Xunit;

namespace Tester.AdoNet.Persistence
{
    [TestCategory("AdoNet"), TestCategory("Persistence"), TestCategory("Sqlite"), TestCategory("Functional")]
    public class SqlitePersistenceGrainStorageTests : IClassFixture<SqlitePersistenceGrainStorageFixture>
    {
        private readonly SqlitePersistenceGrainStorageFixture fixture;
        private readonly CommonStorageTests commonStorageTests;

        public SqlitePersistenceGrainStorageTests(SqlitePersistenceGrainStorageFixture fixture)
        {
            this.fixture = fixture;
            this.commonStorageTests = new CommonStorageTests(fixture.Storage);
        }

        [Fact]
        public async Task WriteRead()
        {
            var (grainType, grainId, grainState) = StorageDataSetPlain<long>.GetTestData(0);
            await this.commonStorageTests.Store_WriteRead(grainType, grainId, grainState);
        }

        [Fact]
        public async Task WriteClearRead()
        {
            var (grainType, grainId, grainState) = StorageDataSetPlain<long>.GetTestData(1);
            await this.commonStorageTests.Store_WriteClearRead(grainType, grainId, grainState);
        }

        [Fact]
        public async Task WriteDuplicateFailsWithInconsistentStateException()
        {
            var exception = await this.commonStorageTests.PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException();
            CommonStorageUtilities.AssertRelationalInconsistentExceptionMessage(exception.Message);
        }

        [Fact]
        public async Task WriteInconsistentFailsWithIncosistentStateException()
        {
            var exception = await this.commonStorageTests.PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException();
            CommonStorageUtilities.AssertRelationalInconsistentExceptionMessage(exception.Message);
        }

        [Fact]
        public async Task ClearInconsistentFailsWithInconsistentStateException()
        {
            var storage = await this.fixture.CreateGrainStorageAsync($"SqliteClearInconsistent-{Guid.NewGuid():N}");
            const string grainType = "sqlite-clear-inconsistent-grain";
            var grainId = GrainId.Create(GrainType.Create(grainType), GrainIdKeyExtensions.CreateIntegerKey(Random.Shared.NextInt64()));

            var grainState = new GrainState<TestState1> { State = new TestState1 { A = "initial", B = 1, C = 1 } };
            await storage.WriteStateAsync(grainType, grainId, grainState);

            grainState.State = new TestState1 { A = "latest", B = 2, C = 2 };
            await storage.WriteStateAsync(grainType, grainId, grainState);
            var staleVersion = (int.Parse(Assert.IsType<string>(grainState.ETag), CultureInfo.InvariantCulture) - 1).ToString(CultureInfo.InvariantCulture);

            var staleState = new GrainState<TestState1> { State = new TestState1(), ETag = staleVersion, RecordExists = true };
            var exception = await Record.ExceptionAsync(() => storage.ClearStateAsync(grainType, grainId, staleState));
            var inconsistent = Assert.IsType<InconsistentStateException>(exception);
            CommonStorageUtilities.AssertRelationalInconsistentExceptionMessage(inconsistent.Message);

            var readState = new GrainState<TestState1> { State = new TestState1() };
            await storage.ReadStateAsync(grainType, grainId, readState);
            Assert.True(readState.RecordExists);
            Assert.Equal(grainState.State, readState.State);
        }

        [Fact]
        public async Task HashCollisionWriteReadWriteRead()
        {
            var storage = await this.fixture.CreateGrainStorageAsync($"SqliteHashCollision-{Guid.NewGuid():N}");
            storage.HashPicker = new StorageHasherPicker(new[] { new ConstantHasher() });
            var storageTests = new CommonStorageTests(storage);
            await storageTests.PersistenceStorage_WriteReadWriteReadStatesInParallel(nameof(HashCollisionWriteReadWriteRead), 2);
        }

        [Fact]
        public async Task ExtensionStringIdentityMatching()
        {
            var storage = await this.fixture.CreateGrainStorageAsync($"SqliteExtension-{Guid.NewGuid():N}");
            storage.HashPicker = new StorageHasherPicker(new[] { new ConstantHasher() });

            const string grainType = "sqlite-extension-string-sensitive-grain";
            const long grainKey = 71337;
            var grainIdA = GrainId.Create(GrainType.Create(grainType), GrainIdKeyExtensions.CreateIntegerKey(grainKey, "A"));
            var grainIdB = GrainId.Create(GrainType.Create(grainType), GrainIdKeyExtensions.CreateIntegerKey(grainKey, "B"));
            var grainStateA = new GrainState<TestState1> { State = new TestState1 { A = "alpha", B = 10, C = 20 } };
            var grainStateB = new GrainState<TestState1> { State = new TestState1 { A = "beta", B = 30, C = 40 } };

            await storage.WriteStateAsync(grainType, grainIdA, grainStateA);
            await storage.WriteStateAsync(grainType, grainIdB, grainStateB);

            var readA = new GrainState<TestState1> { State = new TestState1() };
            var readB = new GrainState<TestState1> { State = new TestState1() };
            await storage.ReadStateAsync(grainType, grainIdA, readA);
            await storage.ReadStateAsync(grainType, grainIdB, readB);

            Assert.Equal(grainStateA.State, readA.State);
            Assert.Equal(grainStateB.State, readB.State);
            Assert.NotEqual(readA.State, readB.State);
        }

        [Fact]
        public async Task ConcurrentFirstWriteRaceUsesOptimisticConcurrency()
        {
            var storage = await this.fixture.CreateGrainStorageAsync($"SqliteConcurrentFirstWrite-{Guid.NewGuid():N}");
            const string grainType = "sqlite-concurrent-first-write-grain";
            var inconsistentStateExceptionCount = 0;

            for (var i = 0; i < 20; i++)
            {
                var grainId = GrainId.Create(GrainType.Create(grainType), GrainIdKeyExtensions.CreateIntegerKey(Random.Shared.NextInt64()));
                var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                var firstWrite = Task.Run(async () =>
                {
                    await startGate.Task;
                    var state = new GrainState<TestState1> { State = new TestState1 { A = "first", B = i, C = i } };
                    return await Record.ExceptionAsync(() => storage.WriteStateAsync(grainType, grainId, state));
                });

                var secondWrite = Task.Run(async () =>
                {
                    await startGate.Task;
                    var state = new GrainState<TestState1> { State = new TestState1 { A = "second", B = i + 1000, C = i + 1000 } };
                    return await Record.ExceptionAsync(() => storage.WriteStateAsync(grainType, grainId, state));
                });

                startGate.SetResult();
                var exceptions = await Task.WhenAll(firstWrite, secondWrite);

                foreach (var exception in exceptions.Where(exception => exception is not null))
                {
                    Assert.IsNotType<SqliteException>(exception);
                    Assert.IsType<InconsistentStateException>(exception);
                    inconsistentStateExceptionCount++;
                }
            }

            Assert.True(inconsistentStateExceptionCount > 0);
        }

        [Fact]
        public async Task VersionProgressionAfterWriteClearWrite()
        {
            const string grainType = "sqlite-version-progression-grain";
            var grainId = GrainId.Create(GrainType.Create(grainType), GrainIdKeyExtensions.CreateIntegerKey(Random.Shared.NextInt64()));
            var grainState = new GrainState<TestState1> { State = new TestState1 { A = "v1", B = 1, C = 1 } };

            await this.fixture.Storage.WriteStateAsync(grainType, grainId, grainState);
            var versionAfterFirstWrite = int.Parse(Assert.IsType<string>(grainState.ETag), CultureInfo.InvariantCulture);

            grainState.State = new TestState1 { A = "v2", B = 2, C = 2 };
            await this.fixture.Storage.WriteStateAsync(grainType, grainId, grainState);
            var versionAfterSecondWrite = int.Parse(Assert.IsType<string>(grainState.ETag), CultureInfo.InvariantCulture);
            Assert.Equal(versionAfterFirstWrite + 1, versionAfterSecondWrite);

            await this.fixture.Storage.ClearStateAsync(grainType, grainId, grainState);
            var versionAfterClear = int.Parse(Assert.IsType<string>(grainState.ETag), CultureInfo.InvariantCulture);
            Assert.Equal(versionAfterSecondWrite + 1, versionAfterClear);

            grainState.State = new TestState1 { A = "v3", B = 3, C = 3 };
            await this.fixture.Storage.WriteStateAsync(grainType, grainId, grainState);
            var versionAfterWritePostClear = int.Parse(Assert.IsType<string>(grainState.ETag), CultureInfo.InvariantCulture);
            Assert.Equal(versionAfterClear + 1, versionAfterWritePostClear);
        }
    }
}
