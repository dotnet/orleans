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
    [TestCategory("Persistence"), TestCategory("MySql")]
    public class PersistenceGrainTests_MySql : GrainPersistenceTestsRunner, IClassFixture<PersistenceGrainTests_MySql.Fixture>
    {
        public const string TestDatabaseName = "OrleansTest_MySql_Storage";
        public const string AdoInvariant = AdoNetInvariants.InvariantNameMySql;
        public const string ConnectionStringKey = "AdoNetConnectionString";
        public static readonly Guid ServiceId = Guid.NewGuid();

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 4;
                builder.Options.UseTestClusterMembership = false;
                var relationalStorage = RelationalStorageForTesting.SetupInstance(AdoInvariant, TestDatabaseName).Result;
                builder.ConfigureHostConfiguration(configBuilder => configBuilder.AddInMemoryCollection(
                    new Dictionary<string, string>
                    {
                        {ConnectionStringKey, relationalStorage.CurrentConnectionString}
                    }));
                builder.Options.ServiceId = ServiceId.ToString();
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
                builder.AddClientBuilderConfigurator<GatewayConnectionTests.ClientBuilderConfigurator>();
            }

            private class MySiloBuilderConfigurator : IHostConfigurator
            {
                public void Configure(IHostBuilder hostBuilder)
                {
                    var connectionString = hostBuilder.GetConfiguration()[ConnectionStringKey];
                    hostBuilder.UseOrleans((ctx, siloBuilder) =>
                    {
                        siloBuilder
                            .UseAdoNetClustering(options =>
                            {
                                options.ConnectionString = connectionString;
                                options.Invariant = AdoInvariant;
                            })
                            .AddAdoNetGrainStorage("GrainStorageForTest", options =>
                            {
                                options.ConnectionString = (string)connectionString;
                                options.Invariant = AdoInvariant;
                            })
                            .AddMemoryGrainStorage("MemoryStore");
                    });
                }
            }
        }

        private readonly Fixture fixture;

        public PersistenceGrainTests_MySql(ITestOutputHelper output, Fixture fixture) : base(output, fixture)
        {
            this.fixture = fixture;
            this.fixture.EnsurePreconditionsMet();
        }
    }
}
