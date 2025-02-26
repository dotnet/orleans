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

namespace Tester.AzureUtils.Migration
{
    [TestCategory("Functionals"), TestCategory("Migration"), TestCategory("Azure"), TestCategory("AzureTableStorage")]
    public class MigrationAzureStorageTableToCosmosDbWithTransformingGrainStorageTests : MigrationTableStorageToCosmosWithTransformGrainTests, IClassFixture<MigrationAzureStorageTableToCosmosDbWithTransformingGrainStorageTests.Fixture>
    {
        public static string OrleansDatabase = Resources.MigrationDatabase;
        public static string OrleansContainer = Resources.MigrationLatestContainer;

        public static string RandomIdentifier = Guid.NewGuid().ToString().Replace("-", "");

        public MigrationAzureStorageTableToCosmosDbWithTransformingGrainStorageTests(Fixture fixture) : base(fixture)
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

                        options.Mode = GrainMigrationMode.ReadDestinationWithFallback_WriteBoth;
                    })
                    .AddAzureTableGrainStorage(SourceStorageName, options =>
                    {
                        options.ConfigureTestDefaults();
                        options.TableName = $"source{RandomIdentifier}";
                    })
                    .AddMigrationAzureCosmosGrainStorage("innerCosmos", options =>
                    {
                        options.ConfigureCosmosStorageOptions();

                        // options.ContainerName = $"destination{RandomIdentifier}";
                        options.ContainerName = OrleansContainer;
                        options.DatabaseName = OrleansDatabase;
                    })
                    .AddTransformingGrainStorage<TestGrainTransformer>(DestinationStorageName, "innerCosmos")
                    .AddDataMigrator(SourceStorageName, DestinationStorageName, new DataMigrator.Options
                    {
                        RunAsBackgroundTask = false // we want to manually call data migrator
                    });
            }
        }

        public class TestGrainTransformer : IGrainStorageTransformer
        {
            private readonly ILogger<TestGrainTransformer> _logger;

            public TestGrainTransformer(ILogger<TestGrainTransformer> logger)
            {
                _logger = logger;
            }

            public void BeforeClearState(ref string grainType, GrainReference grainReference, IGrainState grainState)
            {
            }
            public void BeforeReadState(ref string grainType, GrainReference grainReference, IGrainState grainState)
            {
            }
            public void BeforeWriteState(ref string grainType, GrainReference grainReference, IGrainState grainState)
            {
            }
        }
    }
}
#endif