using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Tester.ClientConnectionTests;
using Tester.StreamingTests;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using TestExtensions.Runners;
using UnitTests.General;
using UnitTests.StorageTests.Relational;
using Xunit;
using Xunit.Abstractions;

namespace Tester.AdoNet.Persistence
{
    [TestCategory("Persistence"), TestCategory("SqlServer")]
    public class PersistenceGrainTests_Sql : GrainPersistenceTestsRunner, IClassFixture<PersistenceGrainTests_Sql.Fixture>
    {
        public const string TestDatabaseName = "OrleansTest";
        public static string AdoInvariant = AdoNetInvariants.InvariantNameSqlServer;
        public static Guid ServiceId = Guid.NewGuid();
        public static string ConnectionStringKey = "AdoNetConnectionString";
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
                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    legacy.ClusterConfiguration.Globals.ServiceId = ServiceId;

                    legacy.ClusterConfiguration.Globals.MaxResendCount = 0;

                    legacy.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("test1");
                    legacy.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("test2",
                        new Dictionary<string, string> { { "Config1", "1" }, { "Config2", "2" } });
                    legacy.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.ErrorInjectionStorageProvider>("ErrorInjector");
                    legacy.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("lowercase");
                });
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
                builder.AddClientBuilderConfigurator<GatewayConnectionTests.ClientBuilderConfigurator>();
            }

            private class MySiloBuilderConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    var connectionString = hostBuilder.GetConfiguration()[ConnectionStringKey];
                    hostBuilder
                        .AddAdoNetGrainStorage("GrainStorageForTest", options =>
                        {
                            options.ConnectionString = (string)connectionString;
                            options.Invariant = AdoInvariant;
                        })
                        .AddMemoryGrainStorage("MemoryStore");
                }
            }
        }

        private Fixture fixture;

        public PersistenceGrainTests_Sql(ITestOutputHelper output, Fixture fixture) : base(output, fixture)
        {
            this.fixture = fixture;
            this.fixture.EnsurePreconditionsMet();
        }
    }
}
