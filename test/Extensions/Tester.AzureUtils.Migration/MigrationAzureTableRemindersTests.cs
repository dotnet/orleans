using Orleans.Hosting;
using Orleans.Persistence.Migration;
using Orleans.TestingHost;
using Tester.AzureUtils.Migration.Abstractions;
using TesterInternal.AzureInfra;
using Xunit;

namespace Tester.AzureUtils.Migration
{
    [TestCategory("Functional"), TestCategory("Migration"), TestCategory("Azure"), TestCategory("AzureTableStorage")]
    public class MigrationAzureTableRemindersTests : MigrationRemindersTests, IClassFixture<MigrationAzureTableRemindersTests.Fixture>
    {
        public static Guid Guid = Guid.NewGuid();

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
                    // -------------------------------------
                    .AddMigrationTools()
                    .UseMigrationAzureTableReminderStorage(
                        oldStorageOptions =>
                        {
                            oldStorageOptions.ConfigureTestDefaults();
                            oldStorageOptions.TableName = OldTableName;
                        },
                        migrationOptions =>
                        {
                            migrationOptions.ConfigureTestDefaults();
                            migrationOptions.TableName = DestinationTableName;
                        }
                    )
                    .AddDataMigrator(SourceStorageName, DestinationStorageName);
            }
        }

        public MigrationAzureTableRemindersTests(Fixture fixture) : base(fixture)
        {
        }
    }
}