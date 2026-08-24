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

    [Fact]
    public async Task RecreatesDatabaseWithActivePooledConnection()
    {
        var storage = await RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNameSqlServer, TestDatabaseName);

        for (var iteration = 0; iteration < 3; iteration++)
        {
            await using var connection = new SqlConnection(storage.CurrentConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";

            Assert.Equal(1, await command.ExecuteScalarAsync());

            storage = await RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNameSqlServer, TestDatabaseName);
        }
    }

    [Fact]
    public async Task SetupOwnsSingleUserSlotWhileApplyingSchema()
    {
        var storage = Assert.IsType<SqlServerStorageForTesting>(
            await RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNameSqlServer, TestDatabaseName));
        await using var competingConnection = new SqlConnection(storage.CurrentConnectionString);
        await competingConnection.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        var reconnectingClient = ReconnectUntilCanceledAsync(storage.CurrentConnectionString, cancellation.Token);

        try
        {
            await storage.ExecuteSetupScriptBatchesAsync(
                [
                    $"""
                    ALTER DATABASE [{TestDatabaseName}] SET READ_COMMITTED_SNAPSHOT OFF;
                    ALTER DATABASE [{TestDatabaseName}] SET READ_COMMITTED_SNAPSHOT ON;
                    """
                ],
                TestDatabaseName);
        }
        finally
        {
            await cancellation.CancelAsync();
            await reconnectingClient;
        }

        var databaseState = await storage.Storage.ReadAsync(
            """
            SELECT user_access_desc, is_read_committed_snapshot_on
            FROM sys.databases
            WHERE name = @DatabaseName
            """,
            command => command.AddParameter("DatabaseName", TestDatabaseName),
            (record, _, _) => Task.FromResult((record.GetString(0), record.GetBoolean(1))));

        Assert.Equal(("MULTI_USER", true), Assert.Single(databaseState));
    }

    private static async Task ReconnectUntilCanceledAsync(string connectionString, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1";
                _ = await command.ExecuteScalarAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
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
