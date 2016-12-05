using Orleans.Runtime.Configuration;
using Orleans.SqlUtils;
using Orleans.TestingHost;
using System.Threading.Tasks;
using UnitTests.General;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.MembershipTests
{
    public class LivenessTests_SqlServer : LivenessTestsBase
    {
        public const string TestDatabaseName = "OrleansTest";
        public LivenessTests_SqlServer(ITestOutputHelper output) : base(output)
        {
        }
        public override TestCluster CreateTestCluster()
        {
            var relationalStorage = RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNameSqlServer, TestDatabaseName).Result;
            var options = new TestClusterOptions(2);
            options.ClusterConfiguration.Globals.DataConnectionString = relationalStorage.CurrentConnectionString;
            options.ClusterConfiguration.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.SqlServer;
            options.ClusterConfiguration.PrimaryNode = null;
            options.ClusterConfiguration.Globals.SeedNodes.Clear();
            return new TestCluster(options);
        }

        [Fact, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task Liveness_SqlServer_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [Fact, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task Liveness_SqlServer_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [Fact, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task Liveness_SqlServer_3_Restartl_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [Fact, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task Liveness_SqlServer_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [Fact, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task Liveness_SqlServer_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }

    public class LivenessTests_PostgreSql : LivenessTestsBase
    {
        public const string TestDatabaseName = "orleanstest";
        public LivenessTests_PostgreSql(ITestOutputHelper output) : base(output)
        {
        }
        public override TestCluster CreateTestCluster()
        {
            var relationalStorage = RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNamePostgreSql, TestDatabaseName).Result;
            var options = new TestClusterOptions(2);
            options.ClusterConfiguration.Globals.DataConnectionString = relationalStorage.CurrentConnectionString;
            options.ClusterConfiguration.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.SqlServer;
            options.ClusterConfiguration.Globals.AdoInvariant = AdoNetInvariants.InvariantNamePostgreSql;
            options.ClusterConfiguration.PrimaryNode = null;
            options.ClusterConfiguration.Globals.SeedNodes.Clear();
            return new TestCluster(options);
        }

        [Fact, TestCategory("Membership"), TestCategory("PostgreSql")]
        public async Task Liveness_PostgreSql_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [Fact, TestCategory("Membership"), TestCategory("PostgreSql")]
        public async Task Liveness_PostgreSql_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [Fact, TestCategory("Membership"), TestCategory("PostgreSql")]
        public async Task Liveness_PostgreSql_3_Restartl_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [Fact, TestCategory("Membership"), TestCategory("PostgreSql")]
        public async Task Liveness_PostgreSql_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [Fact, TestCategory("Membership"), TestCategory("PostgreSql")]
        public async Task Liveness_PostgreSql_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }

    public class LivenessTests_MySql : LivenessTestsBase
    {
        public const string TestDatabaseName = "OrleansTest";
        public LivenessTests_MySql(ITestOutputHelper output) : base(output)
        {
        }
        public override TestCluster CreateTestCluster()
        {
            var relationalStorage = RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNameMySql, TestDatabaseName).Result;
            var options = new TestClusterOptions(2);
            options.ClusterConfiguration.Globals.DataConnectionString = relationalStorage.CurrentConnectionString;
            options.ClusterConfiguration.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.SqlServer;
            options.ClusterConfiguration.Globals.AdoInvariant = AdoNetInvariants.InvariantNameMySql;
            options.ClusterConfiguration.PrimaryNode = null;
            options.ClusterConfiguration.Globals.SeedNodes.Clear();
            return new TestCluster(options);
        }

        [Fact, TestCategory("Membership"), TestCategory("MySql")]
        public async Task Liveness_MySql_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [Fact, TestCategory("Membership"), TestCategory("MySql")]
        public async Task Liveness_MySql_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [Fact, TestCategory("Membership"), TestCategory("MySql")]
        public async Task Liveness_MySql_3_Restartl_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [Fact, TestCategory("Membership"), TestCategory("MySql")]
        public async Task Liveness_MySql_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [Fact, TestCategory("Membership"), TestCategory("MySql")]
        public async Task Liveness_MySql_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }
}
