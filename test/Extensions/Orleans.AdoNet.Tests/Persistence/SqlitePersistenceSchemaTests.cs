using Microsoft.Data.Sqlite;
using Orleans.Runtime;
using Orleans.Storage;
using TestExtensions;
using UnitTests.StorageTests.Relational;
using UnitTests.StorageTests.Relational.TestDataSets;
using Xunit;

namespace Tester.AdoNet.Persistence;

[TestCategory("AdoNet"), TestCategory("Persistence"), TestCategory("Sqlite"), TestCategory("Functional")]
[TestSuite("Functional"), TestProvider("Sqlite"), TestArea("Persistence")]
public sealed class SqlitePersistenceSchemaTests(SqlitePersistenceGrainStorageFixture fixture)
    : IClassFixture<SqlitePersistenceGrainStorageFixture>
{
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "0")]
    [InlineData(true, "0")]
    [InlineData(false, "-1")]
    [InlineData(true, "-1")]
    [InlineData(false, "invalid")]
    [InlineData(true, "invalid")]
    [InlineData(false, "1.0")]
    [InlineData(true, "1.0")]
    [InlineData(false, "2147483648")]
    [InlineData(true, "2147483648")]
    public async Task OutdatedSchemaReportsUpgradeWithoutChangingDatabase(bool useDataSource, string? version)
    {
        using var database = new TestDatabase();
        await database.ApplyScriptsAsync();
        await database.SetSchemaVersionAsync(version);
        using var dataSource = useDataSource ? new TrackingSqliteDataSource(database.ConnectionString) : null;

        var exception = await Assert.ThrowsAsync<OrleansConfigurationException>(() => CreateStorageAsync(database, dataSource));

        Assert.Contains("SqliteSchemaForTest", exception.Message);
        Assert.Contains(version ?? "unversioned", exception.Message);
        Assert.Contains("version 1 or later is required", exception.Message);
        Assert.Contains("Sqlite-Main.sql", exception.Message);
        Assert.Contains("Sqlite-Persistence.sql", exception.Message);
        Assert.Equal(version, await database.ReadSchemaVersionAsync());
        if (dataSource is not null)
        {
            Assert.False(dataSource.IsDisposed);
            Assert.All(dataSource.Connections, connection => Assert.True(connection.IsDisposed));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingSchemaReportsUpgradeWithoutCreatingTables(bool useDataSource)
    {
        using var database = new TestDatabase();
        using var dataSource = useDataSource ? new TrackingSqliteDataSource(database.ConnectionString) : null;
        var exception = await Assert.ThrowsAsync<OrleansConfigurationException>(() => CreateStorageAsync(database, dataSource));

        Assert.Contains("unversioned", exception.Message);
        Assert.Contains("Sqlite-Main.sql", exception.Message);
        Assert.Contains("Sqlite-Persistence.sql", exception.Message);

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';";
        Assert.Equal(0L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, "1")]
    [InlineData(true, "1")]
    [InlineData(false, "2")]
    [InlineData(true, "2")]
    public async Task CompatibleSchemaStartsWithoutChangingQueries(bool useDataSource, string version)
    {
        using var database = new TestDatabase();
        await database.ApplyScriptsAsync();
        await database.SetSchemaVersionAsync(version);
        using var dataSource = useDataSource ? new TrackingSqliteDataSource(database.ConnectionString) : null;
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                ALTER TABLE OrleansQuery RENAME TO TemporaryOrleansQuery;
                ALTER TABLE TemporaryOrleansQuery RENAME TO orleansquery;
                UPDATE OrleansQuery SET QueryText = QueryText || ' /* customized */'
                WHERE QueryKey = 'WriteToStorageKey';
                """;
            Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        var storage = await CreateStorageAsync(database, dataSource);

        Assert.EndsWith(" /* customized */", storage.CurrentOperationalQueries.WriteToStorage);
        Assert.Equal(version, await database.ReadSchemaVersionAsync());
        if (dataSource is not null)
        {
            Assert.Equal(TestContext.Current.CancellationToken, dataSource.LastOpenCancellationToken);
            Assert.False(dataSource.IsDisposed);
            Assert.All(dataSource.Connections, connection => Assert.True(connection.IsDisposed));
        }

        await new CommonStorageTests(storage).Store_WriteClearRead(
            "sqlite-schema",
            GrainId.Create("sqlite-schema", "compatible"),
            new GrainState<TestState1> { State = new TestState1 { A = "current", B = 17, C = 29 } });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReapplyingScriptsUpgradesSchemaAndPreservesState(bool useDataSource)
    {
        using var database = new TestDatabase();
        await database.ApplyScriptsAsync();
        using var dataSource = useDataSource ? new TrackingSqliteDataSource(database.ConnectionString) : null;
        var original = await CreateStorageAsync(database, dataSource);
        var expected = original.CurrentOperationalQueries;
        var grainId = GrainId.Create("sqlite-schema", "preserved");
        var written = new GrainState<TestState1> { State = new TestState1 { A = "preserved", B = 17, C = 29 } };
        await original.WriteStateAsync("sqlite-schema", grainId, written);
        await database.SetSchemaVersionAsync(null);
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE OrleansQuery SET QueryText = 'outdated';
                INSERT INTO OrleansQuery (QueryKey, QueryText) VALUES ('CustomQuery', 'SELECT 42;');
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<OrleansConfigurationException>(() => CreateStorageAsync(database, dataSource));
        await database.ApplyScriptsAsync();
        var upgraded = await CreateStorageAsync(database, dataSource);

        Assert.Equal("1", await database.ReadSchemaVersionAsync());
        Assert.Equal(expected.WriteToStorage, upgraded.CurrentOperationalQueries.WriteToStorage);
        Assert.Equal(expected.ReadFromStorage, upgraded.CurrentOperationalQueries.ReadFromStorage);
        Assert.Equal(expected.ClearState, upgraded.CurrentOperationalQueries.ClearState);

        var read = new GrainState<TestState1> { State = new TestState1() };
        await original.ReadStateAsync("sqlite-schema", grainId, read);
        Assert.True(read.RecordExists);
        Assert.Equal(written.ETag, read.ETag);
        Assert.Equal(written.State, read.State);
        using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT QueryText FROM OrleansQuery WHERE QueryKey = 'CustomQuery';";
        Assert.Equal("SELECT 42;", await verify.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedScriptReapplicationPreservesPreviousSchemaVersion()
    {
        using var database = new TestDatabase();
        await database.ApplyScriptsAsync();
        await database.SetSchemaVersionAsync("0");
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER RejectQueryUpdate BEFORE UPDATE ON OrleansQuery
                WHEN OLD.QueryKey = 'ReadFromStorageKey'
                BEGIN SELECT RAISE(ABORT, 'schema update rejected'); END;
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var exception = await Assert.ThrowsAsync<SqliteException>(() => database.ApplyScriptsAsync());
        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Equal("0", await database.ReadSchemaVersionAsync());
        await Assert.ThrowsAsync<OrleansConfigurationException>(() => CreateStorageAsync(database, null));

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER RejectQueryUpdate;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await database.ApplyScriptsAsync();
        await CreateStorageAsync(database, null);
        Assert.Equal("1", await database.ReadSchemaVersionAsync());
    }

    private Task<AdoNetGrainStorage> CreateStorageAsync(TestDatabase database, TrackingSqliteDataSource? dataSource)
        => dataSource is null
            ? fixture.CreateGrainStorageAsync(TestContext.Current.CancellationToken, "SqliteSchemaForTest", database.ConnectionString)
            : fixture.CreateGrainStorageAsync(dataSource, TestContext.Current.CancellationToken, "SqliteSchemaForTest");

    private sealed class TestDatabase : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"orleans-sqlite-schema-{Guid.NewGuid():N}.db");

        public string ConnectionString => new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Pooling = false
        }.ToString();

        public async Task ApplyScriptsAsync()
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            foreach (var script in new[] { "Sqlite-Main.sql", "Sqlite-Persistence.sql" })
            {
                using var command = connection.CreateCommand();
                command.CommandText = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, script), TestContext.Current.CancellationToken);
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
        }

        public async Task SetSchemaVersionAsync(string? version)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using var command = connection.CreateCommand();
            if (version is null)
            {
                command.CommandText = "DELETE FROM OrleansQuery WHERE QueryKey = 'StorageSchemaVersion';";
            }
            else
            {
                command.CommandText = "UPDATE OrleansQuery SET QueryText = $Version WHERE QueryKey = 'StorageSchemaVersion';";
                command.Parameters.AddWithValue("$Version", version);
            }

            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        public async Task<string?> ReadSchemaVersionAsync()
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT QueryText FROM OrleansQuery WHERE QueryKey = 'StorageSchemaVersion';";
            return (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        public void Dispose() => File.Delete(_path);
    }
}
