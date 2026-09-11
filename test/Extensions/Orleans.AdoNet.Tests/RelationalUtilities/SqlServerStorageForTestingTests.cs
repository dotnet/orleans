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
    [InlineData(3702, true)]
    [InlineData(18456, false)]
    public void ClassifiesRetryableDatabaseResetErrors(int errorNumber, bool expected)
    {
        Assert.Equal(expected, SqlServerStorageForTesting.IsRetryableDatabaseResetError(errorNumber));
    }

    [Theory]
    [InlineData(924, false, true)]
    [InlineData(924, true, false)]
    [InlineData(50924, true, true)]
    [InlineData(1205, false, false)]
    [InlineData(18456, false, false)]
    public void ClassifiesRetryableDatabaseSetupErrors(int errorNumber, bool setupCommandStarted, bool expected)
    {
        Assert.Equal(expected, SqlServerStorageForTesting.IsRetryableDatabaseSetupError(errorNumber, setupCommandStarted));
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
    public async Task SetupOwnsSingleUserSlotWhileApplyingSchema()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storage = Assert.IsType<SqlServerStorageForTesting>(
            await RelationalStorageForTesting.SetupInstance(
                AdoNetInvariants.InvariantNameSqlServer,
                TestDatabaseName,
                cancellationToken: cancellationToken));
        await using var competingConnection = new SqlConnection(storage.CurrentConnectionString);
        await competingConnection.OpenAsync(cancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reconnectAttempts = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var reconnectingClients = reconnectAttempts
            .Select(attempt => ReconnectAndHoldUntilCanceledAsync(
                storage.CurrentConnectionString,
                attempt,
                cancellation.Token))
            .ToArray();

        try
        {
            await Task.WhenAll(reconnectAttempts.Select(attempt => attempt.Task)).WaitAsync(cancellationToken);
            await storage.ExecuteSetupScriptBatchesAsync(
                [
                    $"""
                    ALTER DATABASE [{TestDatabaseName}] SET READ_COMMITTED_SNAPSHOT OFF;
                    ALTER DATABASE [{TestDatabaseName}] SET READ_COMMITTED_SNAPSHOT ON;
                    """,
                    """
                    IF OBJECT_ID(N'[SetupOwnershipBoundary]', 'U') IS NULL
                    CREATE TABLE SetupOwnershipBoundary(Id INT NOT NULL);
                    """
                ],
                TestDatabaseName,
                cancellationToken);
        }
        finally
        {
            await cancellation.CancelAsync();
            await Task.WhenAll(reconnectingClients);
        }

        var databaseState = await storage.Storage.ReadAsync(
            """
            SELECT user_access_desc, is_read_committed_snapshot_on, OBJECT_ID(N'[SetupOwnershipBoundary]', 'U')
            FROM sys.databases
            WHERE name = @DatabaseName
            """,
            command => command.AddParameter("DatabaseName", TestDatabaseName),
            (record, _, _) => Task.FromResult((
                record.GetString(0),
                record.GetBoolean(1),
                record.IsDBNull(2) ? (int?)null : record.GetInt32(2))),
            cancellationToken: cancellationToken);

        var state = Assert.Single(databaseState);
        Assert.Equal("MULTI_USER", state.Item1);
        Assert.True(state.Item2);
        Assert.NotNull(state.Item3);
    }

    private static async Task ReconnectAndHoldUntilCanceledAsync(
        string connectionString,
        TaskCompletionSource reconnectAttempt,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                reconnectAttempt.TrySetResult();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1";
                while (!cancellationToken.IsCancellationRequested)
                {
                    _ = await command.ExecuteScalarAsync(cancellationToken);
                    await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
                }
            }
            catch (SqlException)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }
}
