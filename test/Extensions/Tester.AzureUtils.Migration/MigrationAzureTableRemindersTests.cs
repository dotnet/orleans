using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Persistence.AzureStorage.Migration.Reminders;
using Orleans.Persistence.Migration;
using Orleans.TestingHost;
using Tester.AzureUtils.Migration.Abstractions;
using Orleans.Persistence.AzureStorage.Migration;
using TesterInternal.AzureInfra;
using Xunit;

namespace Tester.AzureUtils.Migration
{
    [TestCategory("Functional"), TestCategory("Migration"), TestCategory("Azure"), TestCategory("AzureTableStorage")]
    public class MigrationAzureTableRemindersTests : MigrationRemindersTests, IClassFixture<MigrationAzureTableRemindersTests.Fixture>
    {
        public static Guid Guid = Guid.NewGuid();

        public MigrationAzureTableRemindersTests(Fixture fixture) : base(fixture)
        {
            fixture.EnsurePreconditionsMet();
        }

        public static string OldTableName => $"source{Guid.ToString().Replace("-", "")}";
        public static string DestinationTableName => $"destination{Guid.ToString().Replace("-", "")}";

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
                    // needed for the OfflineMigrator
                    .AddMigrationGrainStorageAsDefault(options =>
                    {
                        options.SourceStorageName = SourceStorageName;
                        options.DestinationStorageName = DestinationStorageName;
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
                    // reminders
                    .UseAzureTableReminderService("source", options =>
                    {
                        options.ConfigureTestDefaults();
                        options.TableName = OldTableName;
                    })
                    .UseMigrationAzureTableReminderStorage("destination", options =>
                    {
                        options.ConfigureTestDefaults();
                        options.TableName = DestinationTableName;
                    })
                    .UseMigrationReminderTable(options =>
                    {
                        options.SourceReminderTable = "source";
                        options.DestinationReminderTable = "destination";
                        options.Mode = ReminderMigrationMode.ReadDestinationWithFallback_WriteBoth;
                    })
                    .AddDataMigrator(SourceStorageName, DestinationStorageName);
            }
        }
    }
}