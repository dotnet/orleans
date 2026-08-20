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
[TestCategory("SqlServer"), TestCategory("Functional"), TestCategory("AdoNet"), TestCategory("GrainDirectory")]
[TestProvider("SqlServer")]
[TestSuite("Functional")]
public class SqlServerAdoNetGrainDirectoryClusterTests() : AdoNetGrainDirectoryClusterTests(AdoNetInvariants.InvariantNameSqlServer)
{
}

/// <summary>
/// Cluster tests for ADO.NET Grain Directory against PostgreSQL.
/// </summary>
[TestCategory("PostgreSql"), TestCategory("Functional"), TestCategory("AdoNet"), TestCategory("GrainDirectory")]
[TestProvider("PostgreSql")]
[TestSuite("Functional")]
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
[TestCategory("MySql"), TestCategory("Functional"), TestCategory("AdoNet"), TestCategory("GrainDirectory")]
[TestProvider("MySql")]
[TestSuite("Functional")]
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
[TestSuite("Functional")]
[TestArea("GrainDirectory")]
public abstract class AdoNetGrainDirectoryClusterTests : MultipleGrainDirectoriesTests
{
    private const string TestDatabaseName = "OrleansGrainDirectoryTest";

    private static RelationalStorageForTesting _testing = null!;
    private static string _invariant = null!;

    public AdoNetGrainDirectoryClusterTests(string invariant)
    {
        _invariant = invariant;
    }

    public override async ValueTask InitializeAsync()
    {
        // set up the adonet environment before the base initializes
        _testing = await RelationalStorageForTesting.SetupInstance(_invariant, TestDatabaseName);

        Assert.SkipWhen(IsNullOrEmpty(_testing.CurrentConnectionString), $"Database '{TestDatabaseName}' not initialized");

        // base initialization must only happen after the above
        await base.InitializeAsync();
        if (!PreconditionsMet)
        {
            return;
        }
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
