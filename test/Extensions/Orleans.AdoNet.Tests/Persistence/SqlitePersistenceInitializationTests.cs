using Microsoft.Data.Sqlite;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Storage;
using TestExtensions;
using UnitTests.StorageTests.Relational;
using UnitTests.StorageTests.Relational.TestDataSets;
using Xunit;

namespace Tester.AdoNet.Persistence;

[TestCategory("AdoNet"), TestCategory("Persistence"), TestCategory("Sqlite"), TestCategory("Functional")]
[TestSuite("Functional"), TestProvider("Sqlite"), TestArea("Persistence")]
public sealed class SqlitePersistenceInitializationTests(SqlitePersistenceGrainStorageFixture fixture)
    : IClassFixture<SqlitePersistenceGrainStorageFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializationCreatesSchemaAndLoadsQueries(bool useDataSource)
    {
        using var database = new TestDatabase();
        using var dataSource = useDataSource ? new TrackingSqliteDataSource(database.ConnectionString) : null;
        var storage = await CreateStorageAsync(database, dataSource);
        if (dataSource is not null)
        {
            Assert.Equal(TestContext.Current.CancellationToken, dataSource.LastOpenCancellationToken);
        }

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT count(*) FROM sqlite_master
                WHERE name IN ('OrleansQuery', 'OrleansStorage', 'IX_OrleansStorage');
                """;
            Assert.Equal(3L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        await new CommonStorageTests(storage).Store_WriteClearRead(
            "sqlite-initialization",
            GrainId.Create("sqlite-initialization", "new"),
            new GrainState<TestState1> { State = new TestState1 { A = "initialized", B = 17, C = 29 } });

        if (dataSource is not null)
        {
            Assert.False(dataSource.IsDisposed);
            Assert.All(dataSource.Connections, connection => Assert.True(connection.IsDisposed));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InitializationUpdatesQueriesAndPreservesState(bool useDataSource, bool initializeDatabase)
    {
        using var database = new TestDatabase();
        using var dataSource = useDataSource ? new TrackingSqliteDataSource(database.ConnectionString) : null;
        var original = await CreateStorageAsync(database, dataSource);
        var expected = original.CurrentOperationalQueries;
        var grainId = GrainId.Create("sqlite-initialization", "existing");
        var written = new GrainState<TestState1> { State = new TestState1 { A = "preserved", B = 17, C = 29 } };
        await original.WriteStateAsync("sqlite-initialization", grainId, written);

        const string customSuffix = " /* externally managed */";
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE OrleansQuery SET QueryText = QueryText || ' /* externally managed */';
                INSERT INTO OrleansQuery (QueryKey, QueryText) VALUES ('CustomQuery', 'SELECT 42;');
                """;
            Assert.Equal(4, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        var updated = await CreateStorageAsync(database, dataSource, options => options.InitializeSqliteDatabase = initializeDatabase);
        var suffix = initializeDatabase ? string.Empty : customSuffix;
        Assert.Equal(expected.WriteToStorage + suffix, updated.CurrentOperationalQueries.WriteToStorage);
        Assert.Equal(expected.ReadFromStorage + suffix, updated.CurrentOperationalQueries.ReadFromStorage);
        Assert.Equal(expected.ClearState + suffix, updated.CurrentOperationalQueries.ClearState);

        var read = new GrainState<TestState1> { State = new TestState1() };
        await original.ReadStateAsync("sqlite-initialization", grainId, read);
        Assert.True(read.RecordExists);
        Assert.Equal(written.ETag, read.ETag);
        Assert.Equal(written.State, read.State);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT count(*) FROM OrleansStorage;";
            Assert.Equal(1L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            command.CommandText = "SELECT QueryText FROM OrleansQuery WHERE QueryKey = 'CustomQuery';";
            Assert.Equal("SELECT 42;", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task DisabledInitializationRequiresExistingSchema()
    {
        using var database = new TestDatabase();
        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => CreateStorageAsync(database, null, options => options.InitializeSqliteDatabase = false));
        Assert.Equal(1, exception.SqliteErrorCode);
        Assert.Contains("no such table: OrleansQuery", exception.Message);

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name IN ('OrleansQuery', 'OrleansStorage');";
        Assert.Equal(0L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InitializationPropagatesSchemaUpdateFailureAndCanRetry()
    {
        using var database = new TestDatabase();
        var original = await CreateStorageAsync(database, null);
        var expected = original.CurrentOperationalQueries;
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE OrleansQuery SET QueryText = QueryText || ' /* outdated */';
                CREATE TRIGGER RejectQueryUpdate BEFORE UPDATE ON OrleansQuery
                WHEN OLD.QueryKey = 'ReadFromStorageKey'
                BEGIN SELECT RAISE(ABORT, 'schema update rejected'); END;
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var exception = await Assert.ThrowsAsync<SqliteException>(() => CreateStorageAsync(database, null));
        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Contains("schema update rejected", exception.Message);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT QueryText FROM OrleansQuery WHERE QueryKey = 'WriteToStorageKey';";
            Assert.Equal(expected.WriteToStorage, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            command.CommandText = "SELECT QueryText FROM OrleansQuery WHERE QueryKey = 'ReadFromStorageKey';";
            Assert.Equal(expected.ReadFromStorage + " /* outdated */", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            command.CommandText = "DROP TRIGGER RejectQueryUpdate;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var retried = await CreateStorageAsync(database, null);
        Assert.Equal(expected.WriteToStorage, retried.CurrentOperationalQueries.WriteToStorage);
        Assert.Equal(expected.ReadFromStorage, retried.CurrentOperationalQueries.ReadFromStorage);
        Assert.Equal(expected.ClearState, retried.CurrentOperationalQueries.ClearState);
    }

    private Task<AdoNetGrainStorage> CreateStorageAsync(
        TestDatabase database,
        TrackingSqliteDataSource? dataSource,
        Action<AdoNetGrainStorageOptions>? configureOptions = null)
        => fixture.CreateGrainStorageAsync(
            TestContext.Current.CancellationToken,
            $"SqliteInitialization-{Guid.NewGuid():N}",
            options =>
            {
                options.ConnectionString = dataSource is null ? database.ConnectionString : null;
                options.DataSource = dataSource;
                configureOptions?.Invoke(options);
            });

    private sealed class TestDatabase : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"orleans-sqlite-initialization-{Guid.NewGuid():N}.db");

        public string ConnectionString => new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Pooling = false
        }.ToString();

        public void Dispose() => File.Delete(_path);
    }
}
