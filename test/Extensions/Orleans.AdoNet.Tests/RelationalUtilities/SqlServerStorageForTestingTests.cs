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
}
