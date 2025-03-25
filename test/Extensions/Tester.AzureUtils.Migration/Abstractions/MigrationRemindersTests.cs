using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Azure.Data.Tables;
using TestExtensions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Reminders.AzureStorage.Storage.Reminders;
using Orleans.Runtime.ReminderService;
using Orleans.Persistence.Migration;
using Orleans.Persistence.AzureStorage.Migration.Reminders.Storage;

namespace Tester.AzureUtils.Migration.Abstractions
{
    public abstract class MigrationRemindersTests : MigrationBaseTests
    {
        const int baseId = 300;

        private IReminderTableEntryBuilder? migrationEntryBuilder;
        protected IReminderTableEntryBuilder MigrationEntryBuilder
        {
            get
            {
                if (this.migrationEntryBuilder == null)
                {
                    this.migrationEntryBuilder = new MigratedReminderTableEntryBuilder(ServiceProvider.GetRequiredService<IGrainReferenceExtractor>());
                }
                return this.migrationEntryBuilder;
            }
        }

        protected MigrationRemindersTests(BaseAzureTestClusterFixture fixture)
            : base(fixture)
        {
        }

        [Fact]
        public async Task UpsertRow_WritesIntoTwoTables()
        {
            var grain = this.fixture.Client.GetGrain<ISimplePersistentGrain>(baseId + 1);
            var grainState = new GrainState<SimplePersistentGrain_State>(new() { A = 33, B = 806 });
            var stateName = typeof(SimplePersistentGrain).FullName;
            var grainReference = (GrainReference)grain;

            var defaultEntryBuilder = DefaultReminderTableEntryBuilder.Instance;
            var reminderName = GenerateReminderName();
            var migrationEntryRowKey = MigrationEntryBuilder.ConstructRowKey(grainReference, reminderName);
            var oldEntryRowKey = defaultEntryBuilder.ConstructRowKey(grainReference, reminderName);

            var reminderEntry = new ReminderEntry
            {
                GrainRef = grainReference,
                ReminderName = reminderName,
                StartAt = DateTime.UtcNow,
                Period = TimeSpan.FromMinutes(1)
            };

            var reminderTable = await GetAndInitReminderTableAsync();
            var res = await reminderTable.UpsertRow(reminderEntry);

            var migratedTableClient = GetMigratedTableClient();
            var migratedFetchedEntry = migratedTableClient.Query<ReminderTableEntry>(x => x.RowKey == migrationEntryRowKey).FirstOrDefault();
            Assert.NotNull(migratedFetchedEntry);
            Assert.Equal(MigrationEntryBuilder.ConstructPartitionKey(ClusterOptions.ServiceId, grainReference), migratedFetchedEntry!.PartitionKey);
            Assert.Equal(MigrationEntryBuilder.GetGrainReference(grainReference), migratedFetchedEntry!.GrainReference);

            var oldTableClient = GetOldTableClient();
            var oldFetchedEntry = oldTableClient.Query<ReminderTableEntry>(x => x.RowKey == oldEntryRowKey).FirstOrDefault();
            Assert.NotNull(oldFetchedEntry);
            Assert.Equal(defaultEntryBuilder.ConstructPartitionKey(ClusterOptions.ServiceId, grainReference), oldFetchedEntry!.PartitionKey);
            Assert.Equal(defaultEntryBuilder.GetGrainReference(grainReference), oldFetchedEntry!.GrainReference);
        }

        [Fact]
        public async Task Read_ReturnsOriginalGrainReferenceAndReminder()
        {
            var grain = this.fixture.Client.GetGrain<ISimplePersistentGrain>(baseId + 2);
            var grainState = new GrainState<SimplePersistentGrain_State>(new() { A = 33, B = 806 });
            var stateName = typeof(SimplePersistentGrain).FullName;
            var grainReference = (GrainReference)grain;

            var defaultEntryBuilder = DefaultReminderTableEntryBuilder.Instance;
            var reminderName = GenerateReminderName();
            var migrationEntryRowKey = MigrationEntryBuilder.ConstructRowKey(grainReference, reminderName);
            var oldEntryRowKey = defaultEntryBuilder.ConstructRowKey(grainReference, reminderName);

            var reminderEntry = new ReminderEntry
            {
                GrainRef = grainReference,
                ReminderName = reminderName,
                StartAt = DateTime.UtcNow,
                Period = TimeSpan.FromMinutes(1)
            };

            var reminderTable = await GetAndInitReminderTableAsync();
            var res = await reminderTable.UpsertRow(reminderEntry);
            var readEntry = await reminderTable.ReadRow(grainReference, reminderName);
            Assert.NotNull(readEntry);
            Assert.Equal(grainReference.GrainIdentity.PrimaryKey, readEntry.GrainRef.GrainIdentity.PrimaryKey);
            Assert.Equal(reminderName, readEntry.ReminderName);
        }

        [Fact]
        public async Task DataMigrator_ProperlyMigratesData()
        {
            var grain = this.fixture.Client.GetGrain<ISimplePersistentGrain>(baseId + 3);
            var grainState = new GrainState<SimplePersistentGrain_State>(new() { A = 33, B = 806 });
            var stateName = typeof(SimplePersistentGrain).FullName;
            var grainReference = (GrainReference)grain;

            var defaultEntryBuilder = DefaultReminderTableEntryBuilder.Instance;
            var reminderName = GenerateReminderName();
            var migrationEntryRowKey = MigrationEntryBuilder.ConstructRowKey(grainReference, reminderName);
            var oldEntryRowKey = defaultEntryBuilder.ConstructRowKey(grainReference, reminderName);

            var reminderEntry = new ReminderEntry
            {
                GrainRef = grainReference,
                ReminderName = reminderName,
                StartAt = DateTime.UtcNow,
                Period = TimeSpan.FromMinutes(1)
            };

            var reminderTable = await GetAndInitReminderTableAsync();
            var res = await reminderTable.UpsertRow(reminderEntry);

            var stats = await DataMigrator.MigrateRemindersAsync(
                CancellationToken.None,
                startingGrainRefHashCode: grainReference.GrainIdentity.GetUniformHashCode() - 1);

            Assert.NotNull(stats);
            Assert.True(stats.MigratedEntries > 0);
            Assert.Equal((uint)0, stats.FailedEntries);
            Assert.Equal((uint)0, stats.SkippedEntries);
        }

        private static string GenerateReminderName() => "Reminder" + Guid.NewGuid().ToString().Replace("-", "");

        private TableClient GetOldTableClient() => GetTable(MigrationAzureTableRemindersTests.OldTableName);
        private TableClient GetMigratedTableClient() => GetTable(MigrationAzureTableRemindersTests.DestinationTableName);
        private TableClient GetTable(string tableName) => new Azure.Data.Tables.TableClient(TestDefaultConfiguration.DataConnectionString, tableName);
    }
}
