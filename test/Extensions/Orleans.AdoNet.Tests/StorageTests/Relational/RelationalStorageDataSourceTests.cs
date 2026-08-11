using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySql.Data.MySqlClient;
using Npgsql;
using Orleans.Tests.SqlUtils;

namespace UnitTests.StorageTests.Relational;

[TestCategory("AdoNet"), TestCategory("Persistence"), TestCategory("Sqlite")]
public sealed class RelationalStorageDataSourceTests
{
    [Fact]
    public async Task DataSource_ExecutesQueryWithCancellationAndDisposesConnections()
    {
        using var dataSource = new TrackingSqliteDataSource("Data Source=:memory:");
        var storage = RelationalStorage.CreateInstance(AdoNetInvariants.InvariantNameSqlLite, dataSource);
        using var cancellation = new CancellationTokenSource();

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
        Assert.Equal(dataSource.ConnectionString, storage.ConnectionString);
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
        Assert.Equal(dataSource.ConnectionString, storage.ConnectionString);
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
        Assert.Equal(dataSource.ConnectionString, storage.ConnectionString);
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
