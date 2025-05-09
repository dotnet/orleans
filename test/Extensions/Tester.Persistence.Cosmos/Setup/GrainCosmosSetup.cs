using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;
using Orleans.Persistence.AzureStorage.Migration;
using Orleans.Persistence.AzureStorage.Migration.Reminders;
using Orleans.Persistence.Cosmos;
using Orleans.Persistence.Cosmos.DocumentIdProviders;
using Orleans.Persistence.Migration;
using Orleans.TestingHost;
using Tester.Reminders.Cosmos.Migration.Tests;
using Tester.AzureUtils.Migration.Helpers;
using Xunit;
using TesterInternal.AzureInfra;
using Resources = Tester.AzureUtils.Migration.Resources;

namespace Tester.Reminders.Cosmos.Migration.Setup
{
    [TestCategory("Functional"), TestCategory("Migration"), TestCategory("Azure"), TestCategory("Reminders"), TestCategory("Cosmos")]
    public class GrainCosmosSetup : GrainCosmosTests, IClassFixture<GrainCosmosSetup.Fixture>
    {
        public static string RandomIdentifier = Guid.NewGuid().ToString("N");

        public GrainCosmosSetup(Fixture fixture) : base(fixture)
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
                    .AddCosmosGrainStorageAsDefault(options =>
                    {
                        options.ConfigureCosmosStorageOptions();

                        options.ContainerName = Resources.MigrationLatestContainer;
                        options.DatabaseName = Resources.MigrationDatabase;
                    });
            }
        }
    }
}
