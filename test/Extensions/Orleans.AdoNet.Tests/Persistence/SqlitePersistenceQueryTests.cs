using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Tester.AdoNet.Persistence;

[TestSuite("Functional"), TestProvider("Sqlite"), TestArea("Persistence")]
public sealed class SqlitePersistenceQueryTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public SqlitePersistenceQueryTests()
    {
        _connection.Open();
        InitializeDatabase(_connection);
    }

    [Fact]
    public void FailedWriteDoesNotPoisonPooledConnection()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"orleans-sqlite-write-failure-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            DefaultTimeout = 1,
            Pooling = true
        }.ToString();
        var independentConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            DefaultTimeout = 1,
            Pooling = false
        }.ToString();

        try
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                InitializeDatabase(connection);
                Assert.Equal([1], ExecuteQuery(connection, "WriteToStorageKey", null));
            }

            using (var blocker = new SqliteConnection(independentConnectionString))
            {
                blocker.Open();
                using (var command = blocker.CreateCommand())
                {
                    command.CommandText = "BEGIN IMMEDIATE;";
                    command.ExecuteNonQuery();
                }

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    var exception = Assert.Throws<SqliteException>(() => ExecuteQuery(connection, "WriteToStorageKey", 1));
                    Assert.Equal(5, exception.SqliteErrorCode);
                }

                using var rollback = blocker.CreateCommand();
                rollback.CommandText = "ROLLBACK;";
                rollback.ExecuteNonQuery();
            }

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                Assert.Equal([2], ExecuteQuery(connection, "ClearStorageKey", 1));
            }

            AssertStoredState(independentConnectionString, 2, null);

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                Assert.Equal([3], ExecuteQuery(connection, "WriteToStorageKey", 2));
            }

            AssertStoredState(independentConnectionString, 3, "updated");
        }
        finally
        {
            using var connection = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(connection);
            File.Delete(databasePath);
        }
    }

    private static void AssertStoredState(string connectionString, int version, string? payload)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version, CAST(PayloadBinary AS TEXT) FROM OrleansStorage";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(version, reader.GetInt32(0));
        Assert.Equal(payload, reader.IsDBNull(1) ? null : reader.GetString(1));
        Assert.False(reader.Read());
    }

    private static void InitializeDatabase(SqliteConnection connection)
    {
        foreach (var script in new[] { "Sqlite-Main.sql", "Sqlite-Persistence.sql" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, script));
            command.ExecuteNonQuery();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("extension")]
    public void WriteClearWriteReturnsSingleAdvancingVersion(string? extension)
    {
        Assert.Equal([1], ExecuteQuery("WriteToStorageKey", null, extension));
        Assert.Equal([2], ExecuteQuery("WriteToStorageKey", 1, extension));
        Assert.Equal([(2, "updated")], ReadRows());

        Assert.Equal([3], ExecuteQuery("ClearStorageKey", 2, extension));
        Assert.Equal([(3, (string?)null)], ReadRows());

        Assert.Equal([4], ExecuteQuery("WriteToStorageKey", 3, extension));
        Assert.Equal([(4, "updated")], ReadRows());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(null)]
    public void NullVersionWritePreservesExistingState(int? storedVersion)
    {
        InsertRow(storedVersion);

        Assert.Empty(ExecuteQuery("WriteToStorageKey", null));

        Assert.Equal([(storedVersion, "original")], ReadRows());
    }

    [Theory]
    [InlineData("WriteToStorageKey")]
    [InlineData("ClearStorageKey")]
    public void StaleVersionPreservesState(string queryKey)
    {
        InsertRow(9);
        InsertRow(7);

        Assert.Equal([1], ExecuteQuery(queryKey, 1));

        Assert.Equal([(9, "original"), (7, "original")], ReadRows());
    }

    [Theory]
    [InlineData("WriteToStorageKey")]
    [InlineData("ClearStorageKey")]
    public void MissingIdentityWithVersionReturnsConflict(string queryKey)
    {
        Assert.Equal([1], ExecuteQuery(queryKey, 1));

        Assert.Empty(ReadRows());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullVersionClearPreservesState(bool rowExists)
    {
        if (rowExists)
        {
            InsertRow(1);
        }

        Assert.Empty(ExecuteQuery("ClearStorageKey", null));

        if (rowExists)
        {
            Assert.Equal([(1, "original")], ReadRows());
        }
        else
        {
            Assert.Empty(ReadRows());
        }
    }

    [Theory]
    [InlineData("WriteToStorageKey", 1, null)]
    [InlineData("WriteToStorageKey", 9, null)]
    [InlineData("WriteToStorageKey", null, null)]
    [InlineData("ClearStorageKey", 1, null)]
    [InlineData("ClearStorageKey", 9, null)]
    [InlineData("ClearStorageKey", null, null)]
    [InlineData("WriteToStorageKey", 1, "extension")]
    [InlineData("WriteToStorageKey", 9, "extension")]
    [InlineData("WriteToStorageKey", null, "extension")]
    [InlineData("ClearStorageKey", 1, "extension")]
    [InlineData("ClearStorageKey", 9, "extension")]
    [InlineData("ClearStorageKey", null, "extension")]
    public void DuplicateIdentityReturnsVersionOfUpdatedRows(string queryKey, int? duplicateVersion, string? extension)
    {
        // Put the potentially unmatched row first so LIMIT 1 would report the wrong version.
        InsertRow(duplicateVersion, extension);
        InsertRow(1, extension);

        Assert.Equal([2], ExecuteQuery(queryKey, 1, extension));

        var payload = queryKey == "WriteToStorageKey" ? "updated" : null;
        var duplicate = duplicateVersion == 1 ? (2, payload) : (duplicateVersion, "original");
        Assert.Equal([duplicate, (2, payload)], ReadRows());
    }

    [Theory]
    [InlineData("WriteToStorageKey", "GrainIdN0")]
    [InlineData("WriteToStorageKey", "GrainIdN1")]
    [InlineData("WriteToStorageKey", "GrainTypeString")]
    [InlineData("WriteToStorageKey", "GrainIdExtensionString")]
    [InlineData("WriteToStorageKey", "ServiceId")]
    [InlineData("ClearStorageKey", "GrainIdN0")]
    [InlineData("ClearStorageKey", "GrainIdN1")]
    [InlineData("ClearStorageKey", "GrainTypeString")]
    [InlineData("ClearStorageKey", "GrainIdExtensionString")]
    [InlineData("ClearStorageKey", "ServiceId")]
    public void HashCollisionPreservesOtherIdentity(string queryKey, string identityColumn)
    {
        InsertRow(1, differentIdentityColumn: identityColumn);
        Assert.Equal([1], ExecuteQuery("WriteToStorageKey", null));

        Assert.Equal([2], ExecuteQuery(queryKey, 1));

        var payload = queryKey == "WriteToStorageKey" ? "updated" : null;
        Assert.Equal([(1, "original"), (2, payload)], ReadRows());
    }

    private List<int> ExecuteQuery(string queryKey, int? version, string? extension = null)
        => ExecuteQuery(_connection, queryKey, version, extension);

    private static List<int> ExecuteQuery(SqliteConnection connection, string queryKey, int? version, string? extension = null)
    {
        using var lookup = connection.CreateCommand();
        lookup.CommandText = "SELECT QueryText FROM OrleansQuery WHERE QueryKey = @QueryKey";
        lookup.Parameters.AddWithValue("QueryKey", queryKey);

        using var command = connection.CreateCommand();
        command.CommandText = Assert.IsType<string>(lookup.ExecuteScalar());
        AddParameters(command, version, extension);

        using var reader = command.ExecuteReader();
        var versions = new List<int>();
        do
        {
            while (reader.Read())
            {
                Assert.Equal(1, reader.FieldCount);
                Assert.Equal("NewGrainStateVersion", reader.GetName(0));
                versions.Add(reader.GetInt32(0));
            }
        }
        while (reader.NextResult());

        return versions;
    }

    private void InsertRow(int? version, string? extension = null, string? differentIdentityColumn = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO OrleansStorage
                (GrainIdHash, GrainIdN0, GrainIdN1, GrainTypeHash, GrainTypeString,
                 GrainIdExtensionString, ServiceId, PayloadBinary, ModifiedOn, Version)
            VALUES
                (@GrainIdHash, @GrainIdN0, @GrainIdN1, @GrainTypeHash, @GrainTypeString,
                 @GrainIdExtensionString, @ServiceId, @PayloadBinary, datetime('now'), @GrainStateVersion);
            """;
        AddParameters(command, version, extension);
        command.Parameters["PayloadBinary"].Value = Encoding.UTF8.GetBytes("original");
        if (differentIdentityColumn is not null)
        {
            command.Parameters[differentIdentityColumn].Value =
                differentIdentityColumn is "GrainIdN0" or "GrainIdN1" ? 99L : "other";
        }

        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static void AddParameters(SqliteCommand command, int? version, string? extension)
    {
        command.Parameters.AddWithValue("GrainIdHash", 1);
        command.Parameters.AddWithValue("GrainIdN0", 2L);
        command.Parameters.AddWithValue("GrainIdN1", 3L);
        command.Parameters.AddWithValue("GrainTypeHash", 4);
        command.Parameters.AddWithValue("GrainTypeString", "grain-type");
        command.Parameters.AddWithValue("GrainIdExtensionString", (object?)extension ?? DBNull.Value);
        command.Parameters.AddWithValue("ServiceId", "service");
        command.Parameters.AddWithValue("PayloadBinary", Encoding.UTF8.GetBytes("updated"));
        command.Parameters.AddWithValue("GrainStateVersion", (object?)version ?? DBNull.Value);
    }

    private List<(int? Version, string? Payload)> ReadRows()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT Version, CAST(PayloadBinary AS TEXT) FROM OrleansStorage ORDER BY rowid";
        using var reader = command.ExecuteReader();
        var rows = new List<(int? Version, string? Payload)>();
        while (reader.Read())
        {
            rows.Add((reader.IsDBNull(0) ? null : reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return rows;
    }

    public void Dispose() => _connection.Dispose();
}
