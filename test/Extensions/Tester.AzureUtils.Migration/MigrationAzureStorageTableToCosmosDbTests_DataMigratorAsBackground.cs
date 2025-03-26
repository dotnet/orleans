using Orleans.Hosting;
using Orleans.Persistence.Cosmos.Migration;
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
    public class MigrationAzureStorageTableToCosmosDbBackgroundDataMigratorTests : MigrationTableStorageToCosmosTestsWithBackgroundDataMigrator, IClassFixture<MigrationAzureStorageTableToCosmosDbBackgroundDataMigratorTests.Fixture>
    {
        public static string OrleansDatabase = Resources.MigrationDatabase;
        public static string OrleansContainer = Resources.MigrationLatestContainer;

        public static string RandomIdentifier = Guid.NewGuid().ToString("N");

        public MigrationAzureStorageTableToCosmosDbBackgroundDataMigratorTests(Fixture fixture) : base(fixture)
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

                siloBuilder
                    .AddMigrationTools()
                    .AddMigrationGrainStorageAsDefault(options =>
                    {
                        options.SourceStorageName = SourceStorageName;
                        options.DestinationStorageName = DestinationStorageName;

                        options.Mode = GrainMigrationMode.ReadDestinationWithFallback_WriteBoth;
                    })
                    .AddAzureTableGrainStorage(SourceStorageName, options =>
                    {
                        options.ConfigureTestDefaults();
                        options.TableName = $"source{RandomIdentifier}";
                    })
                    .AddMigrationAzureCosmosGrainStorage(DestinationStorageName, options =>
                    {
                        options.ConfigureCosmosStorageOptions();

                        // options.ContainerName = $"destination{RandomIdentifier}";
                        options.ContainerName = OrleansContainer;
                        options.DatabaseName = OrleansDatabase;
                    })
                    .AddDataMigrator(SourceStorageName, DestinationStorageName, options =>
                    {
                        options.BackgroundTaskInitialDelay = TimeSpan.FromSeconds(1);
                    }, runAsBackgroundService: true);
            }
        }
    }
}