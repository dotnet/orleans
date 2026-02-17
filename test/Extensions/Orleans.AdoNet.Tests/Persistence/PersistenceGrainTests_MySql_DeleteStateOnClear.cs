using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using TestExtensions.Runners;
using UnitTests.General;
using Xunit;
using Xunit.Abstractions;

namespace Tester.AdoNet.Persistence
{
    /// <summary>
    /// Tests for Orleans grain state persistence functionality using MySQL
    /// with the delete-state-on-clear option enabled.
    /// </summary>
    [TestCategory("Persistence"), TestCategory("MySql")]
    public class PersistenceGrainTests_MySql_DeleteStateOnClear : GrainPersistenceTestsRunner, IClassFixture<PersistenceGrainTests_MySql_DeleteStateOnClear.Fixture>
    {
        public const string TestDatabaseName = "OrleansTest_MySql_Storage";
        public static string AdoInvariant = AdoNetInvariants.InvariantNameMySql;
        public static string ConnectionStringKey = "AdoNetConnectionString";

        public class Fixture : BaseTestClusterFixture
        {
            protected override void CheckPreconditionsOrThrow()
            {
                if (string.IsNullOrEmpty(TestDefaultConfiguration.MySqlConnectionString))
                {
                    throw new SkipException("MySQL connection string is not specified.");
                }
            }

            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                var relationalStorage = RelationalStorageForTesting.SetupInstance(AdoInvariant, TestDatabaseName).Result;
                builder.ConfigureHostConfiguration(configBuilder => configBuilder.AddInMemoryCollection(
                    new Dictionary<string, string>
                    {
                        {ConnectionStringKey, relationalStorage.CurrentConnectionString}
                    }));
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            }

            private class MySiloBuilderConfigurator : IHostConfigurator
            {
                public void Configure(IHostBuilder hostBuilder)
                {
                    var connectionString = hostBuilder.GetConfiguration()[ConnectionStringKey];
                    hostBuilder.UseOrleans((ctx, siloBuilder) =>
                    {
                        siloBuilder
                            .AddAdoNetGrainStorage("GrainStorageForTest", options =>
                            {
                                options.ConnectionString = connectionString;
                                options.Invariant = AdoInvariant;
                                options.DeleteStateOnClear = true;
                            })
                            .AddMemoryGrainStorage("MemoryStore");
                    });
                }
            }
        }

        public PersistenceGrainTests_MySql_DeleteStateOnClear(ITestOutputHelper output, Fixture fixture) : base(output, fixture)
        {
            DeleteStateOnClear = true;
            DistinguishesGenericGrainTypeParameters = false;
            fixture.EnsurePreconditionsMet();
        }
    }
}
