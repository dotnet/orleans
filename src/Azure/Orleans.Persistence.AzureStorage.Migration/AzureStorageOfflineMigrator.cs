using Microsoft.Extensions.Logging;
using Orleans.Persistence.AzureStorage.Migration.Reminders;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Persistence.Migration
{
    public class AzureStorageOfflineMigrator
    {
        private readonly ILogger<AzureStorageOfflineMigrator> _logger;
        private readonly Options _options;

        private readonly IGrainStorage _oldStorage;
        private readonly IGrainStorage _newStorage;

        readonly MigrationAzureTableReminderStorage _reminderMigrationStorage;

        public AzureStorageOfflineMigrator(
            ILogger<AzureStorageOfflineMigrator> logger,
            IGrainStorage oldStorage,
            IGrainStorage newStorage,
            IReminderTable reminderTable,
            Options options)
        {
            _logger = logger;
            _options = options ?? new Options();

            _oldStorage = oldStorage;
            _newStorage = newStorage;

            _reminderMigrationStorage = reminderTable is MigrationAzureTableReminderStorage migrationAzureTableReminderStorage
                ? migrationAzureTableReminderStorage
                : null;
        }

        /// <summary>
        /// Careful: is a long-time running operation.<br/>
        /// Goes through all the data items in the old storage and migrates them in a new format to the new storage.
        /// </summary>
        public async Task<MigrationStats> MigrateGrainsAsync(CancellationToken cancellationToken)
        {
            _logger.Debug("Starting offline grains migration");

            var migrationStats = new MigrationStats();
            await foreach (var storageEntry in _oldStorage.GetAll(cancellationToken))
            {
                if (!_options.DontSkipMigrateEntries && storageEntry.MigrationEntryClient.IsMigratedEntry)
                {
                    _logger.Debug("Entry {entryName} is already migrated", storageEntry.Name);
                    migrationStats.SkippedEntries++;
                    continue;
                }

                try
                {
                    // if ETag is not nullified, WriteAsync will try to match by the ETag on probably non-existing blob
                    storageEntry.GrainState.ETag = null;

                    await _newStorage.WriteStateAsync(storageEntry.Name, storageEntry.GrainReference, storageEntry.GrainState);
                    await storageEntry.MigrationEntryClient.MarkMigratedAsync(cancellationToken);
                    migrationStats.MigratedEntries++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error migrating grain {GrainType} with reference key='{GrainReference}'", storageEntry.Name, storageEntry.GrainReference.GetPrimaryKey());
                    migrationStats.FailedEntries++;
                }
            }

            _logger.Debug("Finished offline grains migration");
            return migrationStats;
        }

        public async Task<MigrationStats> MigrateRemindersAsync(CancellationToken cancellationToken, uint startingGrainRefHashCode = 0)
        {
            if (_reminderMigrationStorage is null)
            {
                throw new InvalidOperationException("Migration reminder storage is not available. Use 'UseMigrationAzureTableReminderStorage()' to register Reminder's migration component.");
            }

            _logger.Debug("Starting offline reminders migration");
            var migrationStats = new MigrationStats();

            uint currentPointer = startingGrainRefHashCode;
            while (true)
            {
                var entries = await _reminderMigrationStorage.DefaultReminderTable.ReadRows(currentPointer, currentPointer + _options.RemindersMigrationBatchSize);
                _logger.Debug($"Fetched batch: {entries.Reminders.Count} reminders");
                if (entries.Reminders.Count == 0)
                {
                    break;
                }

                foreach (var entry in entries.Reminders)
                {
                    await _reminderMigrationStorage.MigrationReminderTable.UpsertRow(entry);
                    migrationStats.MigratedEntries++;
                }

                currentPointer += _options.RemindersMigrationBatchSize;
            }

            _logger.Debug("Finished offline reminders migration");
            return migrationStats;
        }

        public class MigrationStats
        {
            public int SkippedEntries { get; internal set; }
            public int MigratedEntries { get; internal set; }
            public int FailedEntries { get; internal set; }
        }

        public class Options
        {
            public bool DontSkipMigrateEntries { get; set; } = false;
            public uint RemindersMigrationBatchSize { get; set; } = 10000;
        }
    }
}
