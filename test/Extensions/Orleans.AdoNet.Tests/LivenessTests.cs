using Orleans.Tests.SqlUtils;
using Orleans.TestingHost;
using UnitTests.General;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace UnitTests.MembershipTests
{
    /// <summary>
    /// Tests for Orleans silo membership liveness functionality using SQL Server as the membership provider.
    /// </summary>
    [TestCategory("SqlServer"), TestCategory("Functional"), TestCategory("Membership"), TestCategory("AdoNet")]
    [TestSuite("Functional")]
    [TestProvider("SqlServer")]
    [TestArea("Membership")]
    public class LivenessTests_SqlServer : LivenessTestsBase
    {
        public const string TestDatabaseName = "OrleansTest_SqlServer_Liveness";
        private const string AdoNetInvariantName = AdoNetInvariants.InvariantNameSqlServer;
        private string _connectionString = null!;

        public LivenessTests_SqlServer(ITestOutputHelper output) : base(output)
        {
            EnsurePreconditionsMet();
        }

        protected override void CheckPreconditionsOrThrow() => RelationalStorageForTesting.CheckPreconditionsOrThrow(AdoNetInvariantName);

        public override async ValueTask InitializeAsync()
        {
            var relationalStorage = await RelationalStorageForTesting.SetupInstance(AdoNetInvariantName, TestDatabaseName);
            _connectionString = relationalStorage.CurrentConnectionString;
            await base.InitializeAsync();
            if (!PreconditionsMet)
            {
                return;
            }
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Properties["RelationalStorageConnectionString"] = _connectionString;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        public class SiloConfigurator : IHostConfigurator
        {
            public void Configure(IHostBuilder hostBuilder)
            {
                var cfg = hostBuilder.GetConfiguration();
                var connectionString = cfg["RelationalStorageConnectionString"]!;
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

        [Fact]
        public async Task Liveness_SqlServer_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [Fact]
        public async Task Liveness_SqlServer_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [Fact]
        public async Task Liveness_SqlServer_3_Restartl_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [Fact]
        public async Task Liveness_SqlServer_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [Fact]
        public async Task Liveness_SqlServer_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }

    /// <summary>
    /// Tests for Orleans silo membership liveness functionality using PostgreSQL as the membership provider.
    /// </summary>
    [TestCategory("PostgreSql"), TestCategory("Functional"), TestCategory("Membership"), TestCategory("AdoNet")]
    [TestSuite("Functional")]
    [TestProvider("PostgreSql")]
    [TestArea("Membership")]
    public class LivenessTests_PostgreSql : LivenessTestsBase
    {
        public const string TestDatabaseName = "OrleansTest_Postgres_Liveness";
        private const string AdoNetInvariantName = AdoNetInvariants.InvariantNamePostgreSql;
        private string _connectionString = null!;

        public LivenessTests_PostgreSql(ITestOutputHelper output) : base(output)
        {
            EnsurePreconditionsMet();
        }

        protected override void CheckPreconditionsOrThrow() => RelationalStorageForTesting.CheckPreconditionsOrThrow(AdoNetInvariantName);

        public override async ValueTask InitializeAsync()
        {
            var relationalStorage = await RelationalStorageForTesting.SetupInstance(AdoNetInvariantName, TestDatabaseName);
            _connectionString = relationalStorage.CurrentConnectionString;
            await base.InitializeAsync();
            if (!PreconditionsMet)
            {
                return;
            }
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Properties["RelationalStorageConnectionString"] = _connectionString;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        public class SiloConfigurator : IHostConfigurator
        {
            public void Configure(IHostBuilder hostBuilder)
            {
                var cfg = hostBuilder.GetConfiguration();
                var connectionString = cfg["RelationalStorageConnectionString"]!;
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

        [Fact]
        public async Task Liveness_PostgreSql_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [Fact]
        public async Task Liveness_PostgreSql_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [Fact]
        public async Task Liveness_PostgreSql_3_Restartl_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [Fact]
        public async Task Liveness_PostgreSql_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [Fact]
        public async Task Liveness_PostgreSql_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }

    /// <summary>
    /// Tests for Orleans silo membership liveness functionality using MySQL as the membership provider.
    /// </summary>
    [TestCategory("MySql"), TestCategory("Functional"), TestCategory("Membership"), TestCategory("AdoNet")]
    [TestSuite("Functional")]
    [TestProvider("MySql")]
    [TestArea("Membership")]
    public class LivenessTests_MySql : LivenessTestsBase
    {
        public const string TestDatabaseName = "OrleansTest_MySql_Liveness";
        private const string AdoNetInvariantName = AdoNetInvariants.InvariantNameMySql;
        private string _connectionString = null!;

        public LivenessTests_MySql(ITestOutputHelper output) : base(output)
        {
            EnsurePreconditionsMet();
        }

        protected override void CheckPreconditionsOrThrow() => RelationalStorageForTesting.CheckPreconditionsOrThrow(AdoNetInvariantName);

        public override async ValueTask InitializeAsync()
        {
            var relationalStorage = await RelationalStorageForTesting.SetupInstance(AdoNetInvariantName, TestDatabaseName);
            _connectionString = relationalStorage.CurrentConnectionString;
            await base.InitializeAsync();
            if (!PreconditionsMet)
            {
                return;
            }
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Properties["RelationalStorageConnectionString"] = _connectionString;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        public class SiloConfigurator : IHostConfigurator
        {
            public void Configure(IHostBuilder hostBuilder)
            {
                var cfg = hostBuilder.GetConfiguration();
                var connectionString = cfg["RelationalStorageConnectionString"]!;
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

        [Fact]
        public async Task Liveness_MySql_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [Fact]
        public async Task Liveness_MySql_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [Fact]
        public async Task Liveness_MySql_3_Restartl_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [Fact]
        public async Task Liveness_MySql_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [Fact]
        public async Task Liveness_MySql_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }
}
