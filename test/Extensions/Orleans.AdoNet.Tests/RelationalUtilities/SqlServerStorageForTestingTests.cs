using Microsoft.Data.SqlClient;
using Orleans.Tests.SqlUtils;
using TestExtensions;

namespace UnitTests.General;

[TestCategory("SqlServer")]
[TestSuite("Functional")]
[TestProvider("SqlServer")]
public class SqlServerStorageForTestingTests
{
    private const string TestDatabaseName = "OrleansSqlServerSetupTest";

    [Theory]
    [InlineData(1205, true)]
    [InlineData(924, true)]
    [InlineData(3702, true)]
    [InlineData(5061, true)]
    [InlineData(5064, true)]
    [InlineData(5069, true)]
    [InlineData(18456, false)]
    public void ClassifiesRetryableDatabaseResetErrors(int errorNumber, bool expected)
    {
        Assert.Equal(expected, SqlServerStorageForTesting.IsRetryableDatabaseResetError(errorNumber));
    }

    [Theory]
    [InlineData(-2, true)]
    [InlineData(924, true)]
    [InlineData(1205, true)]
    [InlineData(3702, true)]
    [InlineData(5061, true)]
    [InlineData(5064, true)]
    [InlineData(5069, true)]
    [InlineData(18456, false)]
    public void ClassifiesRetryableDatabaseSetupErrors(int errorNumber, bool expected)
    {
        Assert.Equal(expected, SqlServerStorageForTesting.IsRetryableDatabaseSetupError(errorNumber));
    }

    [Fact]
    public async Task RecreatesDatabaseWithActivePooledConnection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storage = await RelationalStorageForTesting.SetupInstance(
            AdoNetInvariants.InvariantNameSqlServer,
            TestDatabaseName,
            cancellationToken: cancellationToken);

        for (var iteration = 0; iteration < 3; iteration++)
        {
            await using var connection = new SqlConnection(storage.CurrentConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";

            Assert.Equal(1, await command.ExecuteScalarAsync(cancellationToken));

            storage = await RelationalStorageForTesting.SetupInstance(
                AdoNetInvariants.InvariantNameSqlServer,
                TestDatabaseName,
                cancellationToken: cancellationToken);
        }
    }

    [Fact]
    public async Task SetupConfiguresDatabaseWhileClientsReconnect()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storage = Assert.IsType<SqlServerStorageForTesting>(
            await RelationalStorageForTesting.SetupInstance(
                AdoNetInvariants.InvariantNameSqlServer,
                TestDatabaseName,
                cancellationToken: cancellationToken));
        await using var competingConnection = new SqlConnection(storage.CurrentConnectionString);
        await competingConnection.OpenAsync(cancellationToken);
        using var stopReconnecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reconnectAttempts = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var reconnectingClients = reconnectAttempts
            .Select(attempt => ReconnectAndHoldUntilStoppedAsync(
                storage.CurrentConnectionString,
                attempt,
                stopReconnecting.Token,
                cancellationToken))
            .ToArray();

        try
        {
            await Task.WhenAll(reconnectAttempts.Select(attempt => attempt.Task)).WaitAsync(cancellationToken);
            await storage.ExecuteSetupScriptBatchesAsync(
                [
                    """
                    WAITFOR DELAY '00:00:00.250';
                    IF OBJECT_ID(N'[SetupOwnershipBoundary]', 'U') IS NULL
                    CREATE TABLE SetupOwnershipBoundary(Id INT NOT NULL);
                    """
                ],
                TestDatabaseName,
                cancellationToken);
        }
        finally
        {
            await stopReconnecting.CancelAsync();
            await Task.WhenAll(reconnectingClients);
        }

        var databaseState = await storage.Storage.ReadAsync(
            """
            SELECT user_access_desc, recovery_model_desc, snapshot_isolation_state_desc, is_read_committed_snapshot_on,
                OBJECT_ID(N'[SetupOwnershipBoundary]', 'U')
            FROM sys.databases
            WHERE name = @DatabaseName
            """,
            command => command.AddParameter("DatabaseName", TestDatabaseName),
            (record, _, _) => Task.FromResult((
                record.GetString(0),
                record.GetString(1),
                record.GetString(2),
                record.GetBoolean(3),
                record.IsDBNull(4) ? (int?)null : record.GetInt32(4))),
            cancellationToken: cancellationToken);

        var state = Assert.Single(databaseState);
        Assert.Equal("MULTI_USER", state.Item1);
        Assert.Equal("SIMPLE", state.Item2);
        Assert.Equal("ON", state.Item3);
        Assert.True(state.Item4);
        Assert.NotNull(state.Item5);
    }

    private static async Task ReconnectAndHoldUntilStoppedAsync(
        string connectionString,
        TaskCompletionSource reconnectAttempt,
        CancellationToken stoppingToken,
        CancellationToken cancellationToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new SqlConnection(connectionString);
                // Worker shutdown drains SQL operations; test cancellation still aborts I/O.
                await connection.OpenAsync(cancellationToken);
                reconnectAttempt.TrySetResult();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1";
                while (!stoppingToken.IsCancellationRequested)
                {
                    _ = await command.ExecuteScalarAsync(cancellationToken);
                    await Task.Delay(TimeSpan.FromMilliseconds(10), stoppingToken);
                }
            }
            catch (SqlException)
            {
                if (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(10), stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }
    }
}
