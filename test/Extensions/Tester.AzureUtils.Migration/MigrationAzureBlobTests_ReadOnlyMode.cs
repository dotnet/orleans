#if NET7_0_OR_GREATER
using Orleans.Hosting;
using Orleans.Persistence.Migration;
using Orleans.TestingHost;
using Tester.AzureUtils.Migration.Abstractions;
using Xunit;

namespace Tester.AzureUtils.Migration
{
    [TestCategory("Functionals"), TestCategory("Migration"), TestCategory("Azure"), TestCategory("AzureBlobStorage")]
    public class MigrationAzureBlobWithOriginalStorageReadonlyModeTests : MigrationGrainsReadonlyOriginalStorageTests, IClassFixture<MigrationAzureBlobWithOriginalStorageReadonlyModeTests.Fixture>
    {
        public static Guid Guid = Guid.NewGuid();

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

                        // the latter steps of migration, where original storage is only in readonly mode
                        options.WriteToDestinationOnly = true;
                    })
                    .AddAzureBlobGrainStorage(SourceStorageName, options =>
                    {
                        options.ConfigureTestDefaults();
                        options.ContainerName = $"source{Guid}";
                    })
                    .AddMigrationAzureBlobGrainStorage(DestinationStorageName, options =>
                    {
                        options.ConfigureTestDefaults();
                        options.ContainerName = $"destination{Guid}";
                    })
                    .AddDataMigrator(SourceStorageName, DestinationStorageName);
            }
        }   

        public MigrationAzureBlobWithOriginalStorageReadonlyModeTests(Fixture fixture) : base(fixture)
        {
        }
    }
}
#endif