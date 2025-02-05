#if NET8_0_OR_GREATER
using Orleans.Hosting;
using Orleans.Persistence.Cosmos.Migration;
using Orleans.Persistence.Migration;
using Orleans.TestingHost;
using Tester.AzureUtils.Migration.Abstractions;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils.Migration
{
    [TestCategory("Functionals"), TestCategory("Migration"), TestCategory("Azure"), TestCategory("AzureBlobStorage")]
    public class MigrationReadonlyAzureStorageTableToCosmosDbTests : MigrationGrainsReadonlyOriginalStorageTests, IClassFixture<MigrationReadonlyAzureStorageTableToCosmosDbTests.Fixture>
    {
        public const string OrleansDatabase = "Orleans";
        public const string OrleansContainer = "destinationtest";

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
                    .AddMigrationTools()
                    .AddMigrationGrainStorageAsDefault(options =>
                    {
                        options.SourceStorageName = SourceStorageName;
                        options.DestinationStorageName = DestinationStorageName;

                        options.WriteToDestinationOnly = true; // original storage will not be touched!
                    })
                    .AddAzureTableGrainStorage(SourceStorageName, options =>
                    {
                        options.ConfigureTestDefaults();
                        options.TableName = $"source{RandomIdentifier}";
                    })
                    .AddMigrationAzureCosmosGrainStorage(DestinationStorageName, options =>
                    {
                        // options.ContainerName = $"destination{RandomIdentifier}";
                        options.ContainerName = $"destinationtest";
                        options.DatabaseName = "Orleans";
                        options.ConfigureCosmosClient(TestDefaultConfiguration.CosmosConnectionString);
                    })
                    .AddDataMigrator(SourceStorageName, DestinationStorageName);
            }
        }
    }
}
#endif