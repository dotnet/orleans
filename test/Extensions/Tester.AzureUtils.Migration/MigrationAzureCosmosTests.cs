#if NET8_0_OR_GREATER
using Orleans.Hosting;
using Orleans.Persistence.Cosmos.Migration;
using Orleans.Persistence.Migration;
using Orleans.TestingHost;
using Tester.AzureUtils.Migration.Abstractions;
using Xunit;

namespace Tester.AzureUtils.Migration
{
    [TestCategory("Functionals"), TestCategory("Migration"), TestCategory("Azure"), TestCategory("AzureTableStorage")]
    public class MigrationAzureCosmosTests : MigrationCosmosTests, IClassFixture<MigrationAzureCosmosTests.Fixture>
    {
        public static Guid Guid = Guid.NewGuid();

        public MigrationAzureCosmosTests(Fixture fixture) : base(fixture)
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
                    })
                    .AddCosmosGrainStorage(SourceStorageName, options =>
                    {
                        options.ContainerName = $"source{Guid}";
                        options.ConfigureCosmosClient(""); // TODO secret here!
                    })
                    .AddMigrationAzureCosmosGrainStorage(DestinationStorageName, options =>
                    {
                        options.ContainerName = $"destination{Guid}";
                        options.ConfigureCosmosClient(""); // TODO secret here!
                    })
                    .AddDataMigrator(SourceStorageName, DestinationStorageName);
            }
        }
    }
}
#endif