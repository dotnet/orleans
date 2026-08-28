using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySql.Data.MySqlClient;
using Npgsql;
using Orleans.Tests.SqlUtils;
using PersistenceRelationalStorage = Orleans.Persistence.AdoNet.Storage.RelationalStorage;

namespace UnitTests.StorageTests.Relational;

[TestCategory("AdoNet"), TestCategory("Persistence"), TestCategory("Sqlite")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Persistence")]
public sealed class RelationalStorageDataSourceTests
{
    [Fact]
    public async Task DataSource_ExecutesQueryWithCancellationAndDisposesConnections()
    {
        using var dataSource = new TrackingSqliteDataSource("Data Source=:memory:");
        var storage = RelationalStorage.CreateInstance(AdoNetInvariants.InvariantNameSqlLite, dataSource);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var values = await storage.ReadAsync(
            "SELECT 42;",
            parameterProvider: null,
            (record, _, _) => Task.FromResult(record.GetInt32(0)),
            cancellationToken: cancellation.Token);

        Assert.Equal([42], values);
        Assert.Equal(1, dataSource.OpenConnectionAsyncCallCount);
        Assert.Equal(cancellation.Token, dataSource.LastOpenCancellationToken);
        Assert.Equal(2, dataSource.Connections.Count);
        Assert.All(dataSource.Connections, connection => Assert.True(connection.IsDisposed));
        Assert.False(dataSource.IsDisposed);
    }

    [Fact]
    public async Task RelationalOrleansQueries_UsesDataSource()
    {
        using var dataSource = new TrackingSqliteDataSource("Data Source=:memory:");

        var exception = await Assert.ThrowsAsync<SqliteException>(() =>
            RelationalOrleansQueries.CreateInstance(
                AdoNetInvariants.InvariantNameSqlLite,
                connectionString: null,
                dataSource));

        Assert.Contains("OrleansQuery", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, dataSource.OpenConnectionAsyncCallCount);
        Assert.All(dataSource.Connections, connection => Assert.True(connection.IsDisposed));
        Assert.False(dataSource.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateInstance_RequiresExactlyOneConnectionSource(bool configureBoth)
    {
        using var dataSource = new TrackingSqliteDataSource("Data Source=:memory:");

        var exception = Assert.Throws<ArgumentException>(() => RelationalStorage.CreateInstance(
            AdoNetInvariants.InvariantNameSqlLite,
            configureBoth ? dataSource.ConnectionString : null,
            configureBoth ? dataSource : null));

        Assert.Contains("exactly one", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(dataSource.Connections);
        Assert.Equal(0, dataSource.OpenConnectionAsyncCallCount);
    }

    [Fact]
    public void CreateInstance_RejectsProviderMismatchWithoutOpeningConnection()
    {
        using var dataSource = new TrackingSqliteDataSource("Data Source=:memory:");

        var exception = Assert.Throws<ArgumentException>(() =>
            RelationalStorage.CreateInstance(AdoNetInvariants.InvariantNamePostgreSql, dataSource));

        Assert.Contains(typeof(SqliteConnection).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(NpgsqlConnection).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, dataSource.OpenConnectionAsyncCallCount);
        var validationConnection = Assert.Single(dataSource.Connections);
        Assert.True(validationConnection.IsDisposed);
        Assert.False(dataSource.IsDisposed);
    }

    [Fact]
    public void CreateInstance_AcceptsNativeNpgsqlDataSourceWithoutOpeningConnection()
    {
        const string connectionString = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused";
        using var dataSource = NpgsqlDataSource.Create(connectionString);

        var storage = RelationalStorage.CreateInstance(AdoNetInvariants.InvariantNamePostgreSql, dataSource);

        Assert.Equal(AdoNetInvariants.InvariantNamePostgreSql, storage.InvariantName);
        Assert.Equal("Host=127.0.0.1;Port=1;Database=unused;Username=unused", storage.ConnectionString);
    }

    [Fact]
    public void CreateInstance_AcceptsSqlServerDataSourceWithoutOpeningConnection()
    {
        const string connectionString = "Server=127.0.0.1,1;Database=unused;User Id=unused;******;TrustServerCertificate=True";
        using var dataSource = new ProviderDbDataSource(
            connectionString,
            () => new SqlConnection("Server=127.0.0.1,1;Database=unused;Integrated Security=True;TrustServerCertificate=True"));

        var storage = RelationalStorage.CreateInstance(AdoNetInvariants.InvariantNameSqlServer, dataSource);

        Assert.Equal(AdoNetInvariants.InvariantNameSqlServer, storage.InvariantName);
        Assert.Equal(connectionString, storage.ConnectionString);
    }

    [Fact]
    public void CreateInstance_AcceptsMySqlDataSourceWithoutOpeningConnection()
    {
        const string connectionString = "Server=127.0.0.1;Port=1;Database=unused;User Id=unused;******";
        using var dataSource = new ProviderDbDataSource(
            connectionString,
            () => new MySqlConnection("Server=127.0.0.1;Port=1;Database=unused;User Id=unused"));

        var storage = RelationalStorage.CreateInstance(AdoNetInvariants.InvariantNameMySql, dataSource);

        Assert.Equal(AdoNetInvariants.InvariantNameMySql, storage.InvariantName);
        Assert.Equal(connectionString, storage.ConnectionString);
    }

    [Fact]
    public async Task ReadAsync_ReturnsResultSetsInRequestedIndexOrder()
    {
        using var fixture = new RelationalStorageLifecycleFixture();
        var observedTokens = new List<CancellationToken>();

        var results = await fixture.Storage.ReadAsync(
            "SELECT 'first-a' AS Value UNION ALL SELECT 'first-b'; SELECT 'second' AS Value;",
            parameterProvider: null,
            (record, resultSetIndex, cancellationToken) =>
            {
                observedTokens.Add(cancellationToken);
                return Task.FromResult($"{resultSetIndex}:{record.GetString(0)}");
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["0:first-a", "0:first-b", "1:second"], results);
        Assert.Equal(3, observedTokens.Count);
        Assert.All(observedTokens, token => Assert.Equal(TestContext.Current.CancellationToken, token));
        AssertOperationDisposed(fixture.DataSource, expectReader: true);
    }

    [Fact]
    public async Task ReadAsync_ReportsRecordsAffected()
    {
        using var fixture = new RelationalStorageLifecycleFixture();
        fixture.ExecuteSetup(
            """
            CREATE TABLE Items(Id INTEGER PRIMARY KEY, Value TEXT NOT NULL);
            INSERT INTO Items(Id, Value) VALUES (1, 'one'), (2, 'two'), (3, 'three');
            """);
        var observations = new List<(int ResultSetIndex, int RecordsAffected)>();

        var results = await fixture.Storage.ReadAsync(
            "UPDATE Items SET Value = Value || '-updated' WHERE Id <= 2; SELECT Id, Value FROM Items ORDER BY Id;",
            parameterProvider: null,
            (record, resultSetIndex, _) =>
            {
                observations.Add((resultSetIndex, ((DbDataReader)record).RecordsAffected));
                return Task.FromResult($"{record.GetInt64(0)}:{record.GetString(1)}");
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["1:one-updated", "2:two-updated", "3:three"], results);
        Assert.Equal([(0, 2), (0, 2), (0, 2)], observations);
        Assert.Equal(2L, fixture.ExecuteScalar<long>("SELECT COUNT(*) FROM Items WHERE Value LIKE '%-updated';"));
        AssertOperationDisposed(fixture.DataSource, expectReader: true);
    }

    [Fact]
    public async Task ReadAsync_DisposesResourcesAfterSuccess()
    {
        using var fixture = new RelationalStorageLifecycleFixture();

        var results = await fixture.Storage.ReadAsync(
            "SELECT 17 AS Value UNION ALL SELECT 29;",
            parameterProvider: null,
            (record, resultSetIndex, _) => Task.FromResult((resultSetIndex, record.GetInt32(0))),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([(0, 17), (0, 29)], results);
        AssertOperationDisposed(fixture.DataSource, expectReader: true);
        Assert.False(fixture.DataSource.IsDisposed);
    }

    [Fact]
    public async Task ReadAsync_DisposesResourcesWhenOpenFails()
    {
        var expected = new InvalidOperationException("Simulated open failure.");
        using var fixture = new RelationalStorageLifecycleFixture(expected);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Storage.ReadAsync(
            "SELECT 1;",
            parameterProvider: null,
            (record, _, _) => Task.FromResult(record.GetInt32(0)),
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Same(expected, exception);
        Assert.Equal(1, fixture.DataSource.OpenConnectionAsyncCallCount);
        Assert.Equal(2, fixture.DataSource.Connections.Count);
        Assert.All(fixture.DataSource.Connections, connection => Assert.True(connection.IsDisposed));
        var failedConnection = fixture.DataSource.Connections[1];
        Assert.True(failedConnection.OpenAttempted);
        Assert.Empty(failedConnection.Commands);
        Assert.False(fixture.DataSource.IsDisposed);
    }

    [Fact]
    public async Task ReadAsync_DisposesResourcesWhenExecuteFails()
    {
        using var fixture = new RelationalStorageLifecycleFixture();

        var exception = await Assert.ThrowsAsync<SqliteException>(() => fixture.Storage.ReadAsync(
            "SELECT MissingColumn FROM MissingTable;",
            parameterProvider: null,
            (record, _, _) => Task.FromResult(record.GetInt32(0)),
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("no such table", exception.Message, StringComparison.OrdinalIgnoreCase);
        var command = AssertOperationDisposed(fixture.DataSource, expectReader: false);
        Assert.Equal(1, command.ExecuteReaderAsyncCallCount);
    }

    [Fact]
    public async Task ReadAsync_DisposesResourcesWhenSelectorFails()
    {
        using var fixture = new RelationalStorageLifecycleFixture();
        var expected = new InvalidOperationException("Selector failure.");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Storage.ReadAsync<int>(
            "SELECT 11;",
            parameterProvider: null,
            (_, _, _) => throw expected,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Same(expected, exception);
        var command = AssertOperationDisposed(fixture.DataSource, expectReader: true);
        Assert.Equal(1, command.ExecuteReaderAsyncCallCount);
    }

    [Fact]
    public async Task ReadAsync_RejectsInvalidResultSetIndex()
    {
        using var fixture = new RelationalStorageLifecycleFixture();

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Storage.ReadAsync<string>(
            "SELECT 'accepted'; SELECT 'unexpected';",
            parameterProvider: null,
            (record, resultSetIndex, _) => resultSetIndex switch
            {
                0 => Task.FromResult(record.GetString(0)),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(resultSetIndex),
                    resultSetIndex,
                    "Only the first result set is valid for this selector.")
            },
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("resultSetIndex", exception.ParamName);
        Assert.Equal(1, exception.ActualValue);
        AssertOperationDisposed(fixture.DataSource, expectReader: true);
    }

    [Fact]
    public async Task ReadAsync_PropagatesCancellationWhenProviderSupportsIt()
    {
        using var fixture = new RelationalStorageLifecycleFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var selectorCallCount = 0;

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Storage.ReadAsync<int>(
            "SELECT 5 UNION ALL SELECT 8;",
            parameterProvider: null,
            (record, resultSetIndex, cancellationToken) =>
            {
                Assert.Equal(0, resultSetIndex);
                Assert.Equal(5, record.GetInt32(0));
                Assert.Equal(cancellation.Token, cancellationToken);
                selectorCallCount++;
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(0);
            },
            cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, selectorCallCount);
        var command = AssertOperationDisposed(fixture.DataSource, expectReader: true);
        Assert.Equal(1, command.CancelCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsAffectedRowsAndDisposesResources()
    {
        using var fixture = new RelationalStorageLifecycleFixture();
        fixture.ExecuteSetup(
            """
            CREATE TABLE Items(Id INTEGER PRIMARY KEY, Value TEXT NOT NULL);
            INSERT INTO Items(Id, Value) VALUES (1, 'one'), (2, 'two'), (3, 'three');
            """);

        var affectedRows = await fixture.Storage.ExecuteAsync(
            "UPDATE Items SET Value = upper(Value) WHERE Id <> 2;",
            parameterProvider: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, affectedRows);
        Assert.Equal("ONE,two,THREE", fixture.ExecuteScalar<string>(
            "SELECT group_concat(Value, ',') FROM (SELECT Value FROM Items ORDER BY Id);"));
        var command = AssertOperationDisposed(fixture.DataSource, expectReader: true);
        Assert.Equal(1, command.ExecuteReaderAsyncCallCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateInstance_WithInvalidInvariantName_ThrowsArgumentException(string? invariantName)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            RelationalStorage.CreateInstance(invariantName!, "Data Source=:memory:"));

        Assert.Equal("invariantName", exception.ParamName);
        Assert.Equal(
            "The name of invariant must contain characters (Parameter 'invariantName')",
            exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateInstance_WithInvalidConnectionString_ThrowsArgumentException(string? connectionString)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            RelationalStorage.CreateInstance(AdoNetInvariants.InvariantNameSqlLite, connectionString!));

        Assert.Equal("connectionString", exception.ParamName);
        Assert.Equal(
            "Connection string must contain characters (Parameter 'connectionString')",
            exception.Message);
    }

    [Fact]
    public void CreateInstance_WithConnectionString_ReturnsConfiguredStorage()
    {
        const string connectionString = "Data Source=:memory:";

        var storage = RelationalStorage.CreateInstance(
            AdoNetInvariants.InvariantNameSqlLite,
            connectionString);

        Assert.Equal(AdoNetInvariants.InvariantNameSqlLite, storage.InvariantName);
        Assert.Equal(connectionString, storage.ConnectionString);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateInstance_WithDataSourceAndInvalidInvariantName_ValidatesBeforeOpening(string? invariantName)
    {
        using var dataSource = new TrackingSqliteDataSource("Data Source=:memory:");

        var exception = Assert.Throws<ArgumentException>(() =>
            RelationalStorage.CreateInstance(invariantName!, dataSource));

        Assert.Equal("invariantName", exception.ParamName);
        Assert.Empty(dataSource.Connections);
        Assert.Equal(0, dataSource.OpenConnectionAsyncCallCount);
        Assert.False(dataSource.IsDisposed);
    }

    [Fact]
    public void CreateInstance_WithConnectionStringAndNullDataSource_UsesConnectionString()
    {
        const string connectionString = "Data Source=:memory:";

        var storage = RelationalStorage.CreateInstance(
            AdoNetInvariants.InvariantNameSqlLite,
            connectionString,
            dataSource: null);

        Assert.Equal(AdoNetInvariants.InvariantNameSqlLite, storage.InvariantName);
        Assert.Equal(connectionString, storage.ConnectionString);
    }

    [Fact]
    public async Task ReadAsync_WithNullQuery_ThrowsArgumentNullExceptionBeforeOpening()
    {
        using var dataSource = new TrackingSqliteDataSource("Data Source=:memory:");
        var storage = RelationalStorage.CreateInstance(AdoNetInvariants.InvariantNameSqlLite, dataSource);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => storage.ReadAsync<int>(
            query: null!,
            parameterProvider: null,
            (record, _, _) => Task.FromResult(record.GetInt32(0)),
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("query", exception.ParamName);
        AssertNoConnectionOpened(dataSource);
    }

    [Fact]
    public async Task ReadAsync_WithNullSelector_ThrowsArgumentNullExceptionBeforeOpening()
    {
        using var dataSource = new TrackingSqliteDataSource("Data Source=:memory:");
        var storage = RelationalStorage.CreateInstance(AdoNetInvariants.InvariantNameSqlLite, dataSource);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => storage.ReadAsync<int>(
            "SELECT 1;",
            parameterProvider: null,
            selector: null!,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("selector", exception.ParamName);
        AssertNoConnectionOpened(dataSource);
    }

    [Fact]
    public async Task ExecuteAsync_WithNullQuery_ThrowsArgumentNullExceptionBeforeOpening()
    {
        using var dataSource = new TrackingSqliteDataSource("Data Source=:memory:");
        var storage = RelationalStorage.CreateInstance(AdoNetInvariants.InvariantNameSqlLite, dataSource);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => storage.ExecuteAsync(
            query: null!,
            parameterProvider: null,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("query", exception.ParamName);
        AssertNoConnectionOpened(dataSource);
    }

    [Fact]
    public async Task ReadAsync_WithConnectionStringAndParameterProvider_ReturnsExpectedValue()
    {
        const string connectionString = "Data Source=:memory:";
        var storage = RelationalStorage.CreateInstance(
            AdoNetInvariants.InvariantNameSqlLite,
            connectionString);
        System.Data.IDbDataParameter? capturedParameter = null;
        var parameterProviderCallCount = 0;
        var selectorCallCount = 0;
        var observedResultSetIndex = -1;
        var observedCancellationToken = CancellationToken.None;

        var results = await storage.ReadAsync(
            "SELECT (@value * 3) + 2;",
            command =>
            {
                parameterProviderCallCount++;
                capturedParameter = command.CreateParameter();
                capturedParameter.ParameterName = "@value";
                capturedParameter.DbType = System.Data.DbType.Int32;
                capturedParameter.Direction = System.Data.ParameterDirection.Input;
                capturedParameter.Value = 13;
                command.Parameters.Add(capturedParameter);
            },
            (record, resultSetIndex, cancellationToken) =>
            {
                selectorCallCount++;
                observedResultSetIndex = resultSetIndex;
                observedCancellationToken = cancellationToken;
                return Task.FromResult(record.GetInt64(0));
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([41L], results);
        Assert.Equal(1, parameterProviderCallCount);
        Assert.Equal(1, selectorCallCount);
        Assert.Equal(0, observedResultSetIndex);
        Assert.Equal(TestContext.Current.CancellationToken, observedCancellationToken);
        Assert.NotNull(capturedParameter);
        Assert.Equal("@value", capturedParameter.ParameterName);
        Assert.Equal(13, capturedParameter.Value);
        Assert.Equal(System.Data.DbType.Int32, capturedParameter.DbType);
        Assert.Equal(System.Data.ParameterDirection.Input, capturedParameter.Direction);
        Assert.Equal(connectionString, storage.ConnectionString);
    }

    [Fact]
    public async Task ExecuteAsync_WithConnectionString_ReturnsAffectedRows()
    {
        var databaseName = $"relational-storage-connection-string-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";
        using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync(TestContext.Current.CancellationToken);
        using (var setup = keeper.CreateCommand())
        {
            setup.CommandText =
                """
                CREATE TABLE Items(Id INTEGER PRIMARY KEY, Value TEXT NOT NULL);
                INSERT INTO Items(Id, Value) VALUES (1, 'one'), (2, 'two'), (3, 'three');
                """;
            Assert.Equal(3, await setup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        var storage = RelationalStorage.CreateInstance(
            AdoNetInvariants.InvariantNameSqlLite,
            connectionString);

        var affectedRows = await storage.ExecuteAsync(
            "UPDATE Items SET Value = upper(Value) WHERE Id <> 2;",
            parameterProvider: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, affectedRows);
        using var verification = keeper.CreateCommand();
        verification.CommandText =
            "SELECT group_concat(Value, ',') FROM (SELECT Value FROM Items ORDER BY Id);";
        Assert.Equal("ONE,two,THREE", await verification.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Equal(connectionString, storage.ConnectionString);
    }

    [Fact]
    public async Task ReadAsync_WhenConnectionStringOpenFails_PropagatesProviderException()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"orleans-relational-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "missing.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        try
        {
            var storage = RelationalStorage.CreateInstance(
                AdoNetInvariants.InvariantNameSqlLite,
                connectionString);
            Assert.False(File.Exists(databasePath));

            var exception = await Assert.ThrowsAsync<SqliteException>(() => storage.ReadAsync<int>(
                "SELECT 1;",
                parameterProvider: null,
                (record, _, _) => Task.FromResult(record.GetInt32(0)),
                cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal(14, exception.SqliteErrorCode);
            Assert.Equal(14, exception.SqliteExtendedErrorCode);
            Assert.Contains("unable to open database file", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(databasePath));
            Directory.Delete(directory);
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static LifecycleTrackingSqliteCommand AssertOperationDisposed(
        LifecycleTrackingSqliteDataSource dataSource,
        bool expectReader)
    {
        Assert.Equal(1, dataSource.OpenConnectionAsyncCallCount);
        Assert.Equal(2, dataSource.Connections.Count);
        var validationConnection = dataSource.Connections[0];
        Assert.False(validationConnection.OpenAttempted);
        Assert.True(validationConnection.IsDisposed);
        Assert.Empty(validationConnection.Commands);

        var operationConnection = dataSource.Connections[1];
        Assert.True(operationConnection.OpenAttempted);
        Assert.True(operationConnection.IsDisposed);
        Assert.Equal(System.Data.ConnectionState.Closed, operationConnection.State);
        var command = Assert.Single(operationConnection.Commands);
        Assert.True(command.IsDisposed);
        if (expectReader)
        {
            var reader = Assert.Single(command.Readers);
            Assert.True(reader.IsClosed);
        }
        else
        {
            Assert.Empty(command.Readers);
        }

        Assert.False(dataSource.IsDisposed);
        return command;
    }

    private static void AssertNoConnectionOpened(TrackingSqliteDataSource dataSource)
    {
        Assert.Equal(0, dataSource.OpenConnectionAsyncCallCount);
        var validationConnection = Assert.Single(dataSource.Connections);
        Assert.True(validationConnection.IsDisposed);
        Assert.Equal(System.Data.ConnectionState.Closed, validationConnection.Connection.State);
        Assert.False(dataSource.IsDisposed);
    }
}

internal sealed class TrackingSqliteDataSource(string connectionString) : DbDataSource
{
    private readonly List<TrackedSqliteConnection> _connections = [];

    public override string ConnectionString { get; } = connectionString;

    public IReadOnlyList<TrackedSqliteConnection> Connections => _connections;

    public int OpenConnectionAsyncCallCount { get; private set; }

    public CancellationToken LastOpenCancellationToken { get; private set; }

    public bool IsDisposed { get; private set; }

    protected override DbConnection CreateDbConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        var tracked = new TrackedSqliteConnection(connection);
        connection.Disposed += (_, _) => tracked.IsDisposed = true;
        _connections.Add(tracked);
        return connection;
    }

    protected override async ValueTask<DbConnection> OpenDbConnectionAsync(CancellationToken cancellationToken)
    {
        OpenConnectionAsyncCallCount++;
        LastOpenCancellationToken = cancellationToken;
        var connection = CreateDbConnection();
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            IsDisposed = true;
        }

        base.Dispose(disposing);
    }
}

internal sealed class TrackedSqliteConnection(SqliteConnection connection)
{
    public SqliteConnection Connection { get; } = connection;

    public bool IsDisposed { get; set; }
}

internal sealed class ProviderDbDataSource(string connectionString, Func<DbConnection> createConnection) : DbDataSource
{
    public override string ConnectionString { get; } = connectionString;

    protected override DbConnection CreateDbConnection() => createConnection();
}

internal sealed class RelationalStorageLifecycleFixture : IDisposable
{
    private readonly SqliteConnection _keeper;

    public RelationalStorageLifecycleFixture(Exception? openException = null)
    {
        var databaseName = $"relational-storage-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();
        DataSource = new LifecycleTrackingSqliteDataSource(connectionString, openException);
        Storage = PersistenceRelationalStorage.CreateInstance(
            AdoNetInvariants.InvariantNameSqlLite,
            DataSource);
    }

    public LifecycleTrackingSqliteDataSource DataSource { get; }

    public Orleans.Persistence.AdoNet.Storage.IRelationalStorage Storage { get; }

    public void ExecuteSetup(string sql)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public T ExecuteScalar<T>(string sql)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    public void Dispose()
    {
        DataSource.Dispose();
        _keeper.Dispose();
    }
}

internal sealed class LifecycleTrackingSqliteDataSource(
    string connectionString,
    Exception? openException) : DbDataSource
{
    private readonly List<LifecycleTrackingSqliteConnection> _connections = [];

    public override string ConnectionString { get; } = connectionString;

    public IReadOnlyList<LifecycleTrackingSqliteConnection> Connections => _connections;

    public int OpenConnectionAsyncCallCount { get; private set; }

    public bool IsDisposed { get; private set; }

    protected override DbConnection CreateDbConnection()
    {
        var connection = new LifecycleTrackingSqliteConnection(ConnectionString, openException);
        _connections.Add(connection);
        return connection;
    }

    protected override async ValueTask<DbConnection> OpenDbConnectionAsync(CancellationToken cancellationToken)
    {
        OpenConnectionAsyncCallCount++;
        var connection = CreateDbConnection();
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            IsDisposed = true;
        }

        base.Dispose(disposing);
    }
}

internal sealed class LifecycleTrackingSqliteConnection(
    string connectionString,
    Exception? openException) : SqliteConnection(connectionString)
{
    private readonly List<LifecycleTrackingSqliteCommand> _commands = [];

    public IReadOnlyList<LifecycleTrackingSqliteCommand> Commands => _commands;

    public bool OpenAttempted { get; private set; }

    public bool IsDisposed { get; private set; }

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        OpenAttempted = true;
        return openException is null
            ? base.OpenAsync(cancellationToken)
            : Task.FromException(openException);
    }

    protected override DbCommand CreateDbCommand()
    {
        var command = new LifecycleTrackingSqliteCommand(string.Empty, this);
        _commands.Add(command);
        return command;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            IsDisposed = true;
        }

        base.Dispose(disposing);
    }
}

internal sealed class LifecycleTrackingSqliteCommand(
    string commandText,
    SqliteConnection connection) : SqliteCommand(commandText, connection)
{
    private readonly List<DbDataReader> _readers = [];

    public IReadOnlyList<DbDataReader> Readers => _readers;

    public int ExecuteReaderAsyncCallCount { get; private set; }

    public int CancelCallCount { get; private set; }

    public bool IsDisposed { get; private set; }

    public override void Cancel()
    {
        CancelCallCount++;
        base.Cancel();
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        System.Data.CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        ExecuteReaderAsyncCallCount++;
        var reader = await base.ExecuteDbDataReaderAsync(behavior, cancellationToken);
        _readers.Add(reader);
        return reader;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            IsDisposed = true;
        }

        base.Dispose(disposing);
    }
}
