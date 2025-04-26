using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;
using Orleans.Persistence.Cosmos;
using Orleans.Persistence.Cosmos.DocumentIdProviders;
using Orleans.Persistence.Migration;
using Orleans.TestingHost;
using Tester.Reminders.Cosmos.Migration.Helpers;
using Tester.Reminders.Cosmos.Migration.Tests;
using Xunit;

namespace Tester.Reminders.Cosmos.Migration.Setup
{
    [TestCategory("Functional"), TestCategory("Migration"), TestCategory("Azure"), TestCategory("Reminders"), TestCategory("Cosmos")]
    public class RemindersCosmosSetup : RemindersCosmosTests, IClassFixture<RemindersCosmosSetup.Fixture>
    {
        public static string RandomIdentifier = Guid.NewGuid().ToString("N");

        public RemindersCosmosSetup(Fixture fixture) : base(fixture)
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
                    .UseCosmosReminderService(options =>
                    {
                        options.ConfigureCosmosStorageOptions();

                        options.DatabaseName = "Orleans";
                        options.ContainerName = "OrleansRemindersMigration";
                    });

                
            }
        }
    }
}
