using Azure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Persistence.AzureStorage.Migration.Reminders.Storage;
using Orleans.Persistence.Migration;
using Orleans.Reminders.AzureStorage;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Orleans.Persistence.AzureStorage.Migration.Reminders
{
    /// <summary>
    /// Simple storage provider for writing grain state data to Azure blob storage in JSON format.
    /// Implementation impacts management of migrated and current data.
    /// </summary>
    public class MigrationAzureTableReminderStorage : IReminderTable
    {
        internal IReminderTable DefaultReminderTable { get; }
        internal IReminderTable MigrationReminderTable { get; }

        public MigrationAzureTableReminderStorage(
            IGrainReferenceConverter grainReferenceConverter,
            ILoggerFactory loggerFactory,
            IGrainReferenceExtractor grainReferenceExtractor,
            IOptions<ClusterOptions> clusterOptions,
            IOptions<AzureTableReminderStorageOptions> oldStorageOptions,
            IOptions<AzureTableMigrationReminderStorageOptions> migratedStorageOptions)
        {
            DefaultReminderTable = new AzureBasedReminderTable(grainReferenceConverter, loggerFactory, clusterOptions, oldStorageOptions);
            MigrationReminderTable = new MigrationAzureBasedReminderTable(grainReferenceConverter, grainReferenceExtractor, loggerFactory, clusterOptions, migratedStorageOptions);
        }

        public Task Init() => Task.WhenAll(MigrationReminderTable.Init(), DefaultReminderTable.Init());

        public async Task<string> UpsertRow(ReminderEntry entry)
        {
            await MigrationReminderTable.UpsertRow(entry);
            return await DefaultReminderTable.UpsertRow(entry);
        }

        public async Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag)
        {
            await MigrationReminderTable.RemoveRow(grainRef, reminderName, eTag);
            return await DefaultReminderTable.RemoveRow(grainRef, reminderName, eTag);
        }

        public Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName) => DefaultReminderTable.ReadRow(grainRef, reminderName);
        public Task<ReminderTableData> ReadRows(GrainReference key) => DefaultReminderTable.ReadRows(key);
        public Task<ReminderTableData> ReadRows(uint begin, uint end) => DefaultReminderTable.ReadRows(begin, end);
        public Task TestOnlyClearTable() => DefaultReminderTable.TestOnlyClearTable();

        private class MigrationAzureBasedReminderTable : AzureBasedReminderTable
        {
            public MigrationAzureBasedReminderTable(
                IGrainReferenceConverter grainReferenceConverter,
                IGrainReferenceExtractor grainReferenceExtractor,
                ILoggerFactory loggerFactory,
                IOptions<ClusterOptions> clusterOptions,
                IOptions<AzureTableMigrationReminderStorageOptions> storageOptions)
                : base(grainReferenceConverter, loggerFactory, clusterOptions, storageOptions, new MigratedReminderTableEntryBuilder(grainReferenceExtractor))
            {
            }
        }
    }
}
