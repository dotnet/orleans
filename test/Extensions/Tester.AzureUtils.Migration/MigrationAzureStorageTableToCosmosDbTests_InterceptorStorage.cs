#if NET8_0_OR_GREATER
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
using Orleans.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Cosmos;

namespace Tester.AzureUtils.Migration
{
    [TestCategory("Functionals"), TestCategory("Migration"), TestCategory("Azure"), TestCategory("AzureTableStorage")]
    public class MigrationAzureStorageTableToCosmosDbWithStorageInterceptorTests : MigrationTableStorageToCosmosWithStorageInterceptorTests, IClassFixture<MigrationAzureStorageTableToCosmosDbWithStorageInterceptorTests.Fixture>
    {
        public static string OrleansDatabase = Resources.MigrationDatabase;
        public static string OrleansContainer = Resources.MigrationLatestContainer;

        public static string RandomIdentifier = Guid.NewGuid().ToString().Replace("-", "");

        public MigrationAzureStorageTableToCosmosDbWithStorageInterceptorTests(Fixture fixture) : base(fixture)
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
                        services.AddSingletonNamedService<ICosmosStorageDataInterceptor, StorageDataInterceptor>(DestinationStorageName);
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
                    .AddDataMigrator(SourceStorageName, DestinationStorageName);
            }
        }

        public class StorageDataInterceptor : ICosmosStorageDataInterceptor
        {
            public const string TestPartitionKey = "testpartitionkey";

            public void BeforeCreateItem(ref PartitionKey partitionKey, object payload) => Modify(ref partitionKey, payload);
            public void BeforeReplaceItem(ref PartitionKey partitionKey, object payload) => Modify(ref partitionKey, payload);
            public void BeforeUpsertItem(ref PartitionKey partitionKey, object payload) => Modify(ref partitionKey, payload);

            void Modify(ref PartitionKey partitionKey, object payload)
            {
                var jsonPayload = payload as dynamic;

                // testing adjusting partition key
                jsonPayload.PartitionKey = TestPartitionKey;
                partitionKey = new PartitionKey(TestPartitionKey);

                // testing changing state field
                jsonPayload.State.A = 42;
            }
        }
    }
}
#endif