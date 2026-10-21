using System.Text;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using Orleans.Persistence.TestKit;
using UnitTests.StorageTests.Relational;
using UnitTests.StorageTests.Relational.TestDataSets;
using TestState1 = UnitTests.StorageTests.Relational.TestDataSets.TestState1;
using Xunit;

namespace Tester.AdoNet.Persistence
{
    [TestCategory("AdoNet"), TestCategory("Persistence"), TestCategory("Sqlite"), TestCategory("Functional")]
    [TestSuite("Functional")]
    [TestProvider("Sqlite")]
    [TestArea("Persistence")]
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

        [Fact, TestCategory("Functional"), TestCategory("ModelBased")]
        public async Task GrainStorage_ModelBasedGeneratedConformance()
        {
            var runner = new GrainStorageModelBasedTestRunner(this.fixture.Storage, "Sqlite");

            await runner.RunGeneratedConformanceTests(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task ClearInconsistentFailsWithInconsistentStateException()
        {
            var storage = await this.fixture.CreateGrainStorageAsync(
                TestContext.Current.CancellationToken,
                $"SqliteClearInconsistent-{Guid.NewGuid():N}");
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
        public Task VersionWrapsAfterMaximum() => GrainStorageVersionWrapTests.VersionWrapsAfterMaximum(
            this.fixture.Storage,
            this.fixture.DatabaseStorage,
            deleteStateOnClear: false,
            TestContext.Current.CancellationToken);

        [Fact]
        public async Task ReapplyingScriptRepairsVersionCommittedPastMaximum()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var storage = this.fixture.Storage;
            var key = $"version-repair-{Guid.NewGuid():N}";
            var grainId = GrainId.Create("version-repair", key);
            const string grainType = "sqlite-version-repair-grain";
            var grainState = new GrainState<TestState1> { State = new TestState1 { A = "committed-past-maximum" } };
            await storage.WriteStateAsync(grainType, grainId, grainState, cancellationToken);
            var updated = await this.fixture.DatabaseStorage.ExecuteAsync(
                "UPDATE OrleansStorage SET Version = 2147483648 WHERE GrainIdExtensionString = @GrainKey",
                command => command.AddParameter("GrainKey", key),
                cancellationToken: cancellationToken);
            Assert.Equal(1, updated);

            await this.fixture.InitializeSchemaAsync(cancellationToken);
            var read = new GrainState<TestState1>();
            await storage.ReadStateAsync(grainType, grainId, read, cancellationToken);
            read.State = new TestState1 { A = "written-after-repair" };
            await storage.WriteStateAsync(grainType, grainId, read, cancellationToken);

            Assert.Equal("2", read.ETag);
        }

        [Fact]
        public async Task HashCollisionWriteReadWriteRead()
        {
            var storage = await this.fixture.CreateGrainStorageAsync(
                TestContext.Current.CancellationToken,
                $"SqliteHashCollision-{Guid.NewGuid():N}");
            storage.HashPicker = new StorageHasherPicker(new[] { new ConstantHasher() });
            var storageTests = new CommonStorageTests(storage);
            await storageTests.PersistenceStorage_WriteReadWriteReadStatesInParallel(nameof(HashCollisionWriteReadWriteRead), 2);
        }

        [Fact]
        public async Task ExtensionStringIdentityMatching()
        {
            var storage = await this.fixture.CreateGrainStorageAsync(
                TestContext.Current.CancellationToken,
                $"SqliteExtension-{Guid.NewGuid():N}");
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
        public async Task GrainTypeQualifiedKeyFormat_GrainTypesSharingKeyAreIsolated()
        {
            var storage = await this.CreateConstantHashStorageAsync(AdoNetGrainStorageKeyFormat.GrainTypeQualified);
            await GrainStorageKeyFormatTests.GrainTypesSharingKeyAreIsolated(storage);
        }

        [Fact]
        public async Task GrainTypeQualifiedKeyFormat_StateNamesOfOneGrainAreIsolated()
        {
            var storage = await this.CreateConstantHashStorageAsync(AdoNetGrainStorageKeyFormat.GrainTypeQualified);
            await GrainStorageKeyFormatTests.StateNamesOfOneGrainAreIsolated(storage);
        }

        [Fact]
        public async Task GrainTypeQualifiedKeyFormat_KeysParsingToSameValueAreIsolated()
        {
            var storage = await this.CreateConstantHashStorageAsync(AdoNetGrainStorageKeyFormat.GrainTypeQualified);
            await GrainStorageKeyFormatTests.KeysParsingToSameValueAreIsolated(storage);
        }

        [Fact]
        public async Task GrainTypeQualifiedKeyFormat_KeysOfDifferentKindsAreIsolated()
        {
            var storage = await this.CreateConstantHashStorageAsync(AdoNetGrainStorageKeyFormat.GrainTypeQualified);
            await GrainStorageKeyFormatTests.KeysOfDifferentKindsAreIsolated(storage);
        }

        [Fact]
        public async Task LegacyKeyFormat_SharesRowAcrossGrainTypes()
        {
            var storage = await this.CreateConstantHashStorageAsync(AdoNetGrainStorageKeyFormat.Legacy);
            await GrainStorageKeyFormatTests.LegacyFormatSharesRowAcrossGrainTypes(storage);
        }

        [Fact]
        public async Task GrainTypeQualifiedKeyFormat_GrainTypeAndStateNameBoundariesAreIsolated()
        {
            var storage = await this.CreateConstantHashStorageAsync(AdoNetGrainStorageKeyFormat.GrainTypeQualified);
            await GrainStorageKeyFormatTests.GrainTypeAndStateNameBoundariesAreIsolated(storage);
        }

        [Fact]
        public async Task LegacyKeyFormat_StoresStateNameAndDecodedIntegerKey()
        {
            var stateName = $"legacy-columns-{Guid.NewGuid():N}";
            var row = await this.WriteAndReadKeyColumnsAsync(AdoNetGrainStorageKeyFormat.Legacy, "cart", stateName, GrainIdKeyExtensions.CreateIntegerKey(0x1000, "ext"), stateName);

            Assert.Equal((stateName, 0L, 0x1000L, "ext"), row);
        }

        [Fact]
        public async Task GrainTypeQualifiedKeyFormat_StoresHexOfExactIdentityBytes()
        {
            var grainType = $"cart-{Guid.NewGuid():N}";
            var expectedGrainTypeString = $"{Convert.ToHexString(Encoding.UTF8.GetBytes(grainType))}/7374617465";
            var row = await this.WriteAndReadKeyColumnsAsync(AdoNetGrainStorageKeyFormat.GrainTypeQualified, grainType, "state", GrainIdKeyExtensions.CreateIntegerKey(0x1A, "ext"), expectedGrainTypeString);

            Assert.Equal((expectedGrainTypeString, 0L, 0L, "31412B657874"), row);
        }

        [Fact]
        public async Task GrainTypeQualifiedKeyFormat_RejectsIdentityLongerThanKeyColumns()
        {
            var storage = await this.fixture.CreateGrainStorageAsync(
                TestContext.Current.CancellationToken,
                $"SqliteKeyLength-{Guid.NewGuid():N}",
                AdoNetGrainStorageKeyFormat.GrainTypeQualified);
            var grainType = GrainType.Create("sqlite-key-length");
            var longestKey = GrainId.Create(grainType, IdSpan.Create(new string('k', 256)));
            var tooLongKey = GrainId.Create(grainType, IdSpan.Create(new string('k', 257)));

            await storage.WriteStateAsync("state", longestKey, new GrainState<TestState1> { State = new TestState1 { A = "fits" } });
            await Assert.ThrowsAsync<ArgumentException>(() => storage.WriteStateAsync("state", tooLongKey, new GrainState<TestState1> { State = new TestState1() }));
            await Assert.ThrowsAsync<ArgumentException>(() => storage.ReadStateAsync(new string('s', 256), longestKey, new GrainState<TestState1> { State = new TestState1() }));
        }

        /// <summary>
        /// Writes one state and returns the key columns of the row whose <c>GrainTypeString</c> is
        /// <paramref name="expectedGrainTypeString"/>.
        /// </summary>
        private async Task<(string GrainTypeString, long N0, long N1, string? Extension)> WriteAndReadKeyColumnsAsync(
            AdoNetGrainStorageKeyFormat keyFormat,
            string grainType,
            string stateName,
            IdSpan key,
            string expectedGrainTypeString)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var storage = await this.fixture.CreateGrainStorageAsync(
                cancellationToken,
                $"SqliteKeyColumns-{Guid.NewGuid():N}",
                keyFormat);
            var grainId = GrainId.Create(GrainType.Create(grainType), key);
            await storage.WriteStateAsync(stateName, grainId, new GrainState<TestState1> { State = new TestState1 { A = "columns" } });

            await using var connection = new SqliteConnection(this.fixture.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT GrainTypeString, GrainIdN0, GrainIdN1, GrainIdExtensionString FROM OrleansStorage WHERE GrainTypeString = @GrainTypeString;";
            command.Parameters.AddWithValue("@GrainTypeString", expectedGrainTypeString);

            var rows = new List<(string, long, long, string?)>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            }

            return Assert.Single(rows);
        }

        private async Task<AdoNetGrainStorage> CreateConstantHashStorageAsync(AdoNetGrainStorageKeyFormat keyFormat)
        {
            var storage = await this.fixture.CreateGrainStorageAsync(
                TestContext.Current.CancellationToken,
                $"SqliteKeyFormat-{keyFormat}-{Guid.NewGuid():N}",
                keyFormat);
            storage.HashPicker = new StorageHasherPicker(new[] { new ConstantHasher() });
            return storage;
        }

        [Fact]
        public async Task ConcurrentFirstWriteRaceUsesOptimisticConcurrency()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var storage = await this.fixture.CreateGrainStorageAsync(
                cancellationToken,
                $"SqliteConcurrentFirstWrite-{Guid.NewGuid():N}");
            const string grainType = "sqlite-concurrent-first-write-grain";
            var inconsistentStateExceptionCount = 0;

            for (var i = 0; i < 20; i++)
            {
                var grainId = GrainId.Create(GrainType.Create(grainType), GrainIdKeyExtensions.CreateIntegerKey(Random.Shared.NextInt64()));
                var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                var firstWrite = Task.Run(async () =>
                {
                    await startGate.Task.WaitAsync(cancellationToken);
                    var state = new GrainState<TestState1> { State = new TestState1 { A = "first", B = i, C = i } };
                    return await Record.ExceptionAsync(() => storage.WriteStateAsync(grainType, grainId, state));
                }, cancellationToken);

                var secondWrite = Task.Run(async () =>
                {
                    await startGate.Task.WaitAsync(cancellationToken);
                    var state = new GrainState<TestState1> { State = new TestState1 { A = "second", B = i + 1000, C = i + 1000 } };
                    return await Record.ExceptionAsync(() => storage.WriteStateAsync(grainType, grainId, state));
                }, cancellationToken);

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

        [Fact]
        public async Task DataSource_WriteReadClear_LeavesCallerOwnedDataSourceUsable()
        {
            using var dataSource = new TrackingSqliteDataSource(this.fixture.ConnectionString);
            var storage = await this.fixture.CreateGrainStorageAsync(
                dataSource,
                TestContext.Current.CancellationToken,
                $"SqliteDataSource-{Guid.NewGuid():N}");
            const string grainType = "sqlite-data-source-grain";
            var grainId = GrainId.Create(GrainType.Create(grainType), GrainIdKeyExtensions.CreateIntegerKey(Random.Shared.NextInt64()));
            var written = new GrainState<TestState1> { State = new TestState1 { A = "from-data-source", B = 17, C = 29 } };

            await storage.WriteStateAsync(grainType, grainId, written);
            var read = new GrainState<TestState1> { State = new TestState1() };
            await storage.ReadStateAsync(grainType, grainId, read);

            Assert.True(read.RecordExists);
            Assert.Equal(written.State, read.State);
            Assert.NotNull(read.ETag);

            var etagBeforeClear = read.ETag;
            await storage.ClearStateAsync(grainType, grainId, read);

            Assert.False(read.RecordExists);
            Assert.NotEqual(etagBeforeClear, read.ETag);
            Assert.False(dataSource.IsDisposed);

            await using (var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT 43;";
                Assert.Equal(43L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }

            Assert.True(dataSource.OpenConnectionAsyncCallCount >= 5);
            Assert.All(dataSource.Connections, connection => Assert.True(connection.IsDisposed));
            Assert.False(dataSource.IsDisposed);
        }
    }
}
