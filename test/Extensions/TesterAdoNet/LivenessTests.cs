using Orleans.Tests.SqlUtils;
using Orleans.TestingHost;
using UnitTests.General;
using Xunit.Abstractions;
using Microsoft.Extensions.Hosting;

namespace UnitTests.MembershipTests
{
    [TestCategory("SqlServer"), TestCategory("Functional"), TestCategory("Membership"), TestCategory("AdoNet")]
    public class LivenessTests_SqlServer : LivenessTestsBase
    {
        public const string TestDatabaseName = "OrleansTest_SqlServer_Liveness";
        private const string AdoNetInvariantName = AdoNetInvariants.InvariantNameSqlServer;
        public LivenessTests_SqlServer(ITestOutputHelper output) : base(output)
        {
            EnsurePreconditionsMet();
        }

        protected override void CheckPreconditionsOrThrow() => RelationalStorageForTesting.CheckPreconditionsOrThrow(AdoNetInvariantName);

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            var relationalStorage = RelationalStorageForTesting.SetupInstance(AdoNetInvariantName, TestDatabaseName).GetAwaiter().GetResult();
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
                        options.Invariant = AdoNetInvariantName;
                    });
                });
            }
        }

        [SkippableFact]
        public async Task Liveness_SqlServer_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [SkippableFact]
        public async Task Liveness_SqlServer_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [SkippableFact]
        public async Task Liveness_SqlServer_3_Restartl_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [SkippableFact]
        public async Task Liveness_SqlServer_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [SkippableFact]
        public async Task Liveness_SqlServer_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }

    [TestCategory("PostgreSql"), TestCategory("Functional"), TestCategory("Membership"), TestCategory("AdoNet")]
    public class LivenessTests_PostgreSql : LivenessTestsBase
    {
        public const string TestDatabaseName = "OrleansTest_Postgres_Liveness";
        private const string AdoNetInvariantName = AdoNetInvariants.InvariantNamePostgreSql;
        public LivenessTests_PostgreSql(ITestOutputHelper output) : base(output)
        {
            EnsurePreconditionsMet();
        }

        protected override void CheckPreconditionsOrThrow() => RelationalStorageForTesting.CheckPreconditionsOrThrow(AdoNetInvariantName);

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            var relationalStorage = RelationalStorageForTesting.SetupInstance(AdoNetInvariantName, TestDatabaseName).Result;
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
                        options.Invariant = AdoNetInvariantName;
                    });
                });
            }
        }

        [SkippableFact]
        public async Task Liveness_PostgreSql_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [SkippableFact]
        public async Task Liveness_PostgreSql_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [SkippableFact]
        public async Task Liveness_PostgreSql_3_Restartl_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [SkippableFact]
        public async Task Liveness_PostgreSql_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [SkippableFact]
        public async Task Liveness_PostgreSql_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }

    [TestCategory("MySql"), TestCategory("Functional"), TestCategory("Membership"), TestCategory("AdoNet")]
    public class LivenessTests_MySql : LivenessTestsBase
    {
        public const string TestDatabaseName = "OrleansTest_MySql_Liveness";
        private const string AdoNetInvariantName = AdoNetInvariants.InvariantNamePostgreSql;
        public LivenessTests_MySql(ITestOutputHelper output) : base(output)
        {
            EnsurePreconditionsMet();
        }

        protected override void CheckPreconditionsOrThrow() => RelationalStorageForTesting.CheckPreconditionsOrThrow(AdoNetInvariantName);

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            var relationalStorage = RelationalStorageForTesting.SetupInstance(AdoNetInvariantName, TestDatabaseName).Result;
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
                        options.Invariant = AdoNetInvariantName;
                    });
                });
            }
        }

        [SkippableFact]
        public async Task Liveness_MySql_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [SkippableFact]
        public async Task Liveness_MySql_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [SkippableFact]
        public async Task Liveness_MySql_3_Restartl_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [SkippableFact]
        public async Task Liveness_MySql_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [SkippableFact]
        public async Task Liveness_MySql_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }
}
