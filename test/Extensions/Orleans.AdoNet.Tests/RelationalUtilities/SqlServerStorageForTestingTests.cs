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

    [SkippableFact]
    public async Task RecreatesDatabaseWithPooledConnections()
    {
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var storage = await RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNameSqlServer, TestDatabaseName);

            await using var connection = new SqlConnection(storage.CurrentConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";

            Assert.Equal(1, await command.ExecuteScalarAsync());
        }
    }
}
