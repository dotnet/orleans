#if NET8_0_OR_GREATER
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;
using Orleans.Persistence.Cosmos.DocumentIdProviders;
using Orleans.Persistence.Cosmos;
using Orleans.Persistence.Cosmos.Migration;
using Orleans.Persistence.Migration;
using Orleans.TestingHost;
using Tester.AzureUtils.Migration.Abstractions;
using Tester.AzureUtils.Migration.Helpers;
using Xunit;

namespace Tester.AzureUtils.Migration
{
    [TestCategory("Functionals"), TestCategory("Migration"), TestCategory("Azure"), TestCategory("AzureBlobStorage")]
    public class MigrationReadonlyAzureStorageTableToCosmosDbTests : MigrationGrainsReadonlyOriginalStorageTests, IClassFixture<MigrationReadonlyAzureStorageTableToCosmosDbTests.Fixture>
    {
        public static string OrleansDatabase = Resources.MigrationDatabase;
        public static string OrleansContainer = Resources.MigrationLatestContainer;

        public static string RandomIdentifier = Guid.NewGuid().ToString().Replace("-", "");

        public MigrationReadonlyAzureStorageTableToCosmosDbTests(Fixture fixture) : base(fixture)
        {
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

                        options.Mode = GrainMigrationMode.ReadWriteDestination; // original storage will not be touched!
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
                    .AddDataMigrator(SourceStorageName, DestinationStorageName, new DataMigrator.Options
                    {
                        RunAsBackgroundTask = false // we want to manually call data migrator
                    });
            }
        }
    }
}
#endif