using Orleans.Tests.SqlUtils;
using Orleans.TestingHost;
using System.Threading.Tasks;
using UnitTests.General;
using Xunit;
using Xunit.Abstractions;
using Orleans.Hosting;
using Microsoft.Extensions.Hosting;

namespace UnitTests.MembershipTests
{
    public class LivenessTests_SqlServer : LivenessTestsBase
    {
        public const string TestDatabaseName = "OrleansTest";
        public LivenessTests_SqlServer(ITestOutputHelper output) : base(output)
        {
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            var relationalStorage = RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNameSqlServer, TestDatabaseName).Result;
            builder.Properties["RelationalStorageConnectionString"] = relationalStorage.CurrentConnectionString;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        public class SiloConfigurator : IHostConfigurator
        {
            public void Configure(IHostBuilder hostBuilder)
            {
                var cfg = hostBuilder.GetConfiguration();
                var connectionString = cfg["RelationalStorageConnectionString"];
                hostBuilder.UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder.UseAdoNetClustering(options =>
                    {
                        options.ConnectionString = connectionString;
                        options.Invariant = AdoNetInvariants.InvariantNameSqlServer;
                    });
                });
            }
        }

        [Fact, TestCategory("Membership"), TestCategory("AdoNet")]
        public async Task Liveness_SqlServer_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [Fact, TestCategory("Membership"), TestCategory("AdoNet")]
        public async Task Liveness_SqlServer_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [Fact, TestCategory("Membership"), TestCategory("AdoNet")]
        public async Task Liveness_SqlServer_3_Restartl_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [Fact, TestCategory("Membership"), TestCategory("AdoNet")]
        public async Task Liveness_SqlServer_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [Fact, TestCategory("Membership"), TestCategory("AdoNet")]
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
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            var relationalStorage = RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNamePostgreSql, TestDatabaseName).Result;
            builder.Properties["RelationalStorageConnectionString"] = relationalStorage.CurrentConnectionString;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        public class SiloConfigurator : IHostConfigurator
        {
            public void Configure(IHostBuilder hostBuilder)
            {
                var cfg = hostBuilder.GetConfiguration();
                var connectionString = cfg["RelationalStorageConnectionString"];
                hostBuilder.UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder.UseAdoNetClustering(options =>
                    {
                        options.ConnectionString = connectionString;
                        options.Invariant = AdoNetInvariants.InvariantNamePostgreSql;
                    });
                });
            }
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

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            var relationalStorage = RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNameMySql, TestDatabaseName).Result;
            builder.Properties["RelationalStorageConnectionString"] = relationalStorage.CurrentConnectionString;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        public class SiloConfigurator : IHostConfigurator
        {
            public void Configure(IHostBuilder hostBuilder)
            {
                var cfg = hostBuilder.GetConfiguration();
                var connectionString = cfg["RelationalStorageConnectionString"];
                hostBuilder.UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder.UseAdoNetClustering(options =>
                    {
                        options.ConnectionString = connectionString;
                        options.Invariant = AdoNetInvariants.InvariantNameMySql;
                    });
                });
            }
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
