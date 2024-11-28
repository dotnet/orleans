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
        IReminderTable _oldTable;
        IReminderTable _newTable;

        public MigrationAzureTableReminderStorage(
            IGrainReferenceConverter grainReferenceConverter,
            ILoggerFactory loggerFactory,
            IGrainReferenceExtractor grainReferenceExtractor,
            IOptions<ClusterOptions> clusterOptions,
            IOptions<AzureTableReminderStorageOptions> oldStorageOptions,
            IOptions<AzureTableMigrationReminderStorageOptions> migratedStorageOptions)
        {
            _oldTable = new AzureBasedReminderTable(grainReferenceConverter, loggerFactory, clusterOptions, oldStorageOptions);
            _newTable = new MigrationAzureBasedReminderTable(grainReferenceConverter, grainReferenceExtractor, loggerFactory, clusterOptions, migratedStorageOptions);
        }

        public async Task Init()
        {
            await _newTable.Init();
            await _oldTable.Init();
        }

        public Task<string> UpsertRow(ReminderEntry entry)
        {
            _newTable.UpsertRow(entry).GetAwaiter().GetResult();
            // Task.Run(() => _newTable.UpsertRow(entry)); // run in background is better here probably?
            return _oldTable.UpsertRow(entry);
        }

        public Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag)
        {
            Task.Run(() => _newTable.RemoveRow(grainRef, reminderName, eTag)); // run in background is better here probably?
            return _oldTable.RemoveRow(grainRef, reminderName, eTag);
        }

        public Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName) => _oldTable.ReadRow(grainRef, reminderName);
        public Task<ReminderTableData> ReadRows(GrainReference key) => _oldTable.ReadRows(key);
        public Task<ReminderTableData> ReadRows(uint begin, uint end) => _oldTable.ReadRows(begin, end);
        public Task TestOnlyClearTable() => _oldTable.TestOnlyClearTable();

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
