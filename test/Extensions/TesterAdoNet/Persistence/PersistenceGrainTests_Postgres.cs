using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using TestExtensions.Runners;
using UnitTests.General;
using Xunit;
using Xunit.Abstractions;

namespace Tester.AdoNet.Persistence
{
    [TestCategory("Persistence"), TestCategory("PostgreSql")]
    public class PersistenceGrainTests_Postgres : GrainPersistenceTestsRunner, IClassFixture<PersistenceGrainTests_Postgres.Fixture>
    {
        public const string TestDatabaseName = "OrleansTest_Postgres_Storage";
        public static string AdoInvariant = AdoNetInvariants.InvariantNamePostgreSql;
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

        private Fixture fixture;

        public PersistenceGrainTests_Postgres(ITestOutputHelper output, Fixture fixture) : base(output, fixture)
        {
            this.fixture = fixture;
            this.fixture.EnsurePreconditionsMet();
        }
    }
}
