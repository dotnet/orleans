using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using TestExtensions.Runners;
using UnitTests.General;
using Xunit;

namespace Tester.AdoNet.Persistence
{
    /// <summary>
    /// Tests for Orleans grain state persistence functionality using PostgreSQL as the storage provider.
    /// </summary>
    [TestCategory("Persistence"), TestCategory("PostgreSql")]
    [TestSuite("Functional")]
    [TestProvider("PostgreSql")]
    [TestArea("Persistence")]
    public class PersistenceGrainTests_Postgres : GrainPersistenceTestsRunner, IClassFixture<PersistenceGrainTests_Postgres.Fixture>
    {
        public const string TestDatabaseName = "OrleansTest_Postgres_Storage";
        public static string AdoInvariant = AdoNetInvariants.InvariantNamePostgreSql;
        public static string ConnectionStringKey = "AdoNetConnectionString";

        public class Fixture : BaseTestClusterFixture
        {
            private string _connectionString = null!;

            protected override void CheckPreconditionsOrThrow()
            {
                if (string.IsNullOrEmpty(TestDefaultConfiguration.PostgresConnectionString))
                {
                    throw Xunit.Sdk.SkipException.ForSkip("Postgres connection string is not specified.");
                }
            }

            public override async ValueTask InitializeAsync()
            {
                if (!PreconditionsMet)
                {
                    return;
                }

                var relationalStorage = await RelationalStorageForTesting.SetupInstance(
                    AdoInvariant,
                    TestDatabaseName,
                    cancellationToken: TestContext.Current.CancellationToken);
                _connectionString = relationalStorage.CurrentConnectionString;
                await base.InitializeAsync();
                if (!PreconditionsMet)
                {
                    return;
                }
            }

            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.ConfigureHostConfiguration(configBuilder => configBuilder.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        {ConnectionStringKey, _connectionString}
                    }));
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            }

            private class MySiloBuilderConfigurator : IHostConfigurator
            {
                public void Configure(IHostBuilder hostBuilder)
                {
                    var connectionString = hostBuilder.GetConfiguration()[ConnectionStringKey]!;

                    hostBuilder.UseOrleans((ctx, siloBuilder) =>
                    {
                        siloBuilder
                            .AddAdoNetGrainStorage("GrainStorageForTest", options =>
                            {
                                options.ConnectionString = connectionString;
                                options.Invariant = AdoInvariant;
                            })
                            .AddMemoryGrainStorage("MemoryStore");
                    });
                }
            }
        }

        public PersistenceGrainTests_Postgres(ITestOutputHelper output, Fixture fixture) : base(output, fixture)
        {
            DistinguishesGenericGrainTypeParameters = false;
            fixture.EnsurePreconditionsMet();
        }
    }
}
