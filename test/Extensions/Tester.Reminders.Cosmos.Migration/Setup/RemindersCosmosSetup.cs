using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Persistence.AzureStorage.Migration;
using Orleans.Persistence.AzureStorage.Migration.Reminders;
using Orleans.Persistence.Cosmos;
using Orleans.Persistence.Cosmos.DocumentIdProviders;
using Orleans.Persistence.Migration;
using Orleans.TestingHost;
using Tester.Reminders.Cosmos.Migration.Helpers;
using Tester.Reminders.Cosmos.Migration.Tests;
using Xunit;
using TesterInternal.AzureInfra;
using Resources = Tester.AzureUtils.Migration.Resources;

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
                    .UseAzureTableReminderService("source", options =>
                    {
                        options.ConfigureTestDefaults();
                        options.TableName = $"source{Guid.NewGuid().ToString("N")}"; // we will not call it anyway in tests. Tests only check COSMOS reminder service functionality
                    })
                    .UseCosmosReminderService("destination", options =>
                    {
                        options.ConfigureCosmosStorageOptions();

                        options.ContainerName = Resources.MigrationLatestContainer;
                        options.DatabaseName = Resources.MigrationDatabase;
                    })
                    .UseMigrationReminderTable(options =>
                    {
                        options.SourceReminderTable = "source";
                        options.DestinationReminderTable = "destination";
                        options.Mode = ReminderMigrationMode.ReadDestinationWithFallback_WriteBoth;
                    });
            }
        }
    }
}
