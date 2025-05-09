using Orleans.Hosting;
using Orleans.Persistence.Migration;
using Orleans.TestingHost;
using Tester.AzureUtils.Migration.Abstractions;
using Xunit;
using Orleans;
using Tester.AzureUtils.Migration.Helpers;
using Orleans.Persistence.Cosmos;
using Orleans.Persistence.Cosmos.DocumentIdProviders;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TesterInternal.AzureInfra;

namespace Tester.AzureUtils.Migration
{
    [TestCategory("Functional"), TestCategory("Migration"), TestCategory("Azure"), TestCategory("AzureTableStorage")]
    public class MigrationAzureStorageTableToCosmosDbTestsWithStorageRegistrations : MigrationTableStorageToCosmosTestsWithStorageRegistrations, IClassFixture<MigrationAzureStorageTableToCosmosDbTestsWithStorageRegistrations.Fixture>
    {
        public static string OrleansDatabase = Resources.MigrationDatabase;
        public static string OrleansContainer = Resources.MigrationLatestContainer;

        public static string RandomIdentifier = Guid.NewGuid().ToString("N");

        public MigrationAzureStorageTableToCosmosDbTestsWithStorageRegistrations(Fixture fixture) : base(fixture)
        {
            fixture.EnsurePreconditionsMet();
        }

        public class Fixture : BaseAzureTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            }
        }

        private class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder siloBuilder)
            {
                siloBuilder
                    .ConfigureServices(services =>
                    {
                        services.TryAddSingleton<IDocumentIdProvider, ClusterDocumentIdProvider>();
                    });

                siloBuilder.AddMigrationTools();

                siloBuilder
                    .AddDataMigrator(Source1, Destination1, name: Migration1)
                    .AddMigrationGrainStorage(Migration1, options =>
                    {
                        options.SourceStorageName = Source1;
                        options.DestinationStorageName = Destination1;

                        options.Mode = GrainMigrationMode.ReadDestinationWithFallback_WriteBoth;
                    })
                    .AddAzureTableGrainStorage(Source1, options =>
                    {
                        options.ConfigureTestDefaults();
                        options.TableName = $"source1{RandomIdentifier}";
                    })
                    .AddCosmosGrainStorage(Destination1, options =>
                    {
                        options.ConfigureCosmosStorageOptions();

                        options.ContainerName = OrleansContainer;
                        options.DatabaseName = OrleansDatabase;
                    });

                siloBuilder
                    .AddDataMigrator(Source2, Destination2, name: Migration2)
                    .AddMigrationGrainStorage(Migration2, options =>
                    {
                        options.SourceStorageName = Source2;
                        options.DestinationStorageName = Destination2;

                        options.Mode = GrainMigrationMode.ReadDestinationWithFallback_WriteBoth;
                    })
                    .AddAzureTableGrainStorage(Source2, options =>
                    {
                        options.ConfigureTestDefaults();
                        options.TableName = $"source2{RandomIdentifier}";
                    })
                    .AddCosmosGrainStorage(Destination2, options =>
                    {
                        options.ConfigureCosmosStorageOptions();

                        options.ContainerName = OrleansContainer;
                        options.DatabaseName = OrleansDatabase;
                    });
            }
        }
    }
}