#if NET70
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Persistence.Migration;
using Orleans.TestingHost;
using Tester.AzureUtils.Migration.Abstractions;
using Xunit;

namespace Tester.AzureUtils.Migration
{
    [TestCategory("Functionals"), TestCategory("Migration"), TestCategory("Azure"), TestCategory("AzureTableStorage")]
    public class MigrationAzureTableTests : MigrationRemindersBaseTests, IClassFixture<MigrationAzureTableTests.Fixture>
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

        public MigrationAzureTableTests(Fixture fixture) : base(fixture)
        {
        }
    }
}
#endif
