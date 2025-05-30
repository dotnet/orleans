using MySql.Data.MySqlClient;
using Npgsql;
using Orleans.TestingHost;
using Orleans.Tests.SqlUtils;
using Tester.Directories;
using UnitTests.General;
using UnitTests.Grains.Directories;
using static System.String;

namespace Tester.AdoNet.GrainDirectory;

/// <summary>
/// Cluster tests for ADO.NET Grain Directory against SQL Server.
/// </summary>
[TestCategory("SqlServer")]
public class SqlServerAdoNetGrainDirectoryClusterTests() : AdoNetGrainDirectoryClusterTests(AdoNetInvariants.InvariantNameSqlServer)
{
}

/// <summary>
/// Cluster tests for ADO.NET Grain Directory against PostgreSQL.
/// </summary>
[TestCategory("PostgreSql")]
public class PostgreSqlAdoNetGrainDirectoryClusterTests : AdoNetGrainDirectoryClusterTests
{
    public PostgreSqlAdoNetGrainDirectoryClusterTests() : base(AdoNetInvariants.InvariantNamePostgreSql)
    {
        NpgsqlConnection.ClearAllPools();
    }
}

/// <summary>
/// Cluster tests for ADO.NET Grain Directory against MySQL.
/// </summary>
[TestCategory("MySql")]
public class MySqlAdoNetGrainDirectoryClusterTests : AdoNetGrainDirectoryClusterTests
{
    public MySqlAdoNetGrainDirectoryClusterTests() : base(AdoNetInvariants.InvariantNameMySql)
    {
        MySqlConnection.ClearAllPools();
    }
}

/// <summary>
/// Cluster tests base class for ADO.NET Grain Directory.
/// </summary>
[TestCategory("Functional"), TestCategory("AdoNet"), TestCategory("GrainDirectory")]
public abstract class AdoNetGrainDirectoryClusterTests : MultipleGrainDirectoriesTests
{
    private const string TestDatabaseName = "OrleansGrainDirectoryTest";

    private static RelationalStorageForTesting _testing;
    private static string _invariant;

    public AdoNetGrainDirectoryClusterTests(string invariant)
    {
        _invariant = invariant;
    }

    public override async Task InitializeAsync()
    {
        // set up the adonet environment before the base initializes
        _testing = await RelationalStorageForTesting.SetupInstance(_invariant, TestDatabaseName);

        Skip.If(IsNullOrEmpty(_testing.CurrentConnectionString), $"Database '{TestDatabaseName}' not initialized");

        // base initialization must only happen after the above
        await base.InitializeAsync();
    }

    public class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddAdoNetGrainDirectory(CustomDirectoryGrain.DIRECTORY, options =>
            {
                options.Invariant = _invariant;
                options.ConnectionString = _testing.CurrentConnectionString;
            });
        }
    }

    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        base.ConfigureTestCluster(builder);

        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
    }
}
