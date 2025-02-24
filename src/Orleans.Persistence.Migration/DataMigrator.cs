using Azure;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Persistence.Migration
{
    public class DataMigrator
    {
        private readonly ILogger<DataMigrator> _logger;
        private readonly Options _options;

        private readonly IExtendedGrainStorage _sourceStorage;
        private readonly IGrainStorage _destinationStorage;

        readonly IReminderMigrationTable _reminderMigrationStorage;

        public DataMigrator(
            ILogger<DataMigrator> logger,
            IGrainStorage sourceStorage,
            IGrainStorage destinationStorage,
            IReminderMigrationTable reminderMigrationTable,
            Options options)
        {
            _logger = logger;
            _options = options ?? new Options();

            // instead of doing re-registrations of same storage, we can just check if it's already IGrainStorageEntriesController
            // if not - we simply fail fast with an explicit error message 
            _sourceStorage = (sourceStorage is IExtendedGrainStorage oldStorageEntriesController)
                ? oldStorageEntriesController
                : throw new ArgumentException($"Implement {nameof(IExtendedGrainStorage)} on grainStorage to support data migration.", paramName: nameof(sourceStorage));
            _destinationStorage = destinationStorage;

            _reminderMigrationStorage = reminderMigrationTable;
        }

        /// <summary>
        /// Careful: is a long-time running operation.<br/>
        /// Goes through all the data items in the old storage and migrates them in a new format to the new storage.
        /// </summary>
        public async Task<MigrationStats> MigrateGrainsAsync(CancellationToken cancellationToken)
        {
            _logger.Info("Starting grains migration");

            var migrationStats = new MigrationStats();
            await foreach (var storageEntry in _sourceStorage.GetAll(cancellationToken))
            {
                if (!_options.DontSkipMigrateEntries)
                {
                    IGrainState tmpState = new GrainState<object>();
                    await _destinationStorage.ReadStateAsync(storageEntry.GrainType, storageEntry.GrainReference, tmpState);

                    if (tmpState is not null && tmpState.RecordExists)
                    {
                        _logger.Info("Entry (type='{grainType}';ref='{reference}') already exists at destination storage", storageEntry.GrainType, storageEntry.GrainReference);
                        migrationStats.SkippedEntries++;
                        continue;
                    }
                }

                try
                {
                    // if ETag is not nullified, WriteAsync will try to match by the ETag on probably non-existing blob
                    storageEntry.GrainState.ETag = null;

                    try
                    {
                        if (_destinationStorage is IMigrationGrainStorage migrationGrainStorage)
                        {
                            // sometimes the storage does not allow direct writing (i.e. CosmosDB with it's GrainActivationContext dependency)
                            // meaning we should use a special method to write a grain state
                            await migrationGrainStorage.MigrateGrainStateAsync(storageEntry.GrainType, storageEntry.GrainReference, storageEntry.GrainState);
                        }
                        else
                        {
                            await _destinationStorage.WriteStateAsync(storageEntry.GrainType, storageEntry.GrainReference, storageEntry.GrainState);
                        }
                    }
                    // guarding against any exception which can happen against different storages (i.e. storage/cosmos/etc) here
                    catch (InconsistentStateException ex) when (ex.InnerException is RequestFailedException reqExc && reqExc.Message.StartsWith("The specified blob already exists"))
                    {
                        _logger.Info("Migrated blob already exists, but was not skipped: {entryName};", storageEntry.GrainType);
                        // ignore: we have already migrated this entry to new storage.
                    }
                    catch (InconsistentStateException ex) when (ex.Message.Contains("Resource with specified id or name already exists"))
                    {
                        _logger.Info("Migrated cosmosDb doc already exists, but was not skipped: {entryName};", storageEntry.GrainType);
                        // ignore: we have already migrated this entry to new storage.
                    }

                    migrationStats.MigratedEntries++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error migrating grain {GrainType} with reference key='{GrainReference}'", storageEntry.GrainType, storageEntry.GrainReference.GetPrimaryKey());
                    migrationStats.FailedEntries++;
                }
            }

            _logger.Info("Finished grains migration");
            return migrationStats;
        }

        public async Task<MigrationStats> MigrateRemindersAsync(CancellationToken cancellationToken, uint startingGrainRefHashCode = 0)
        {
            if (_reminderMigrationStorage is null)
            {
                throw new InvalidOperationException("Migration reminder storage is not available. Use 'UseMigrationAzureTableReminderStorage()' to register Reminder's migration component.");
            }

            _logger.Info("Starting reminders migration");
            var migrationStats = new MigrationStats();

            uint currentPointer = startingGrainRefHashCode;
            while (true)
            {
                try
                {
                    var entries = await _reminderMigrationStorage.SourceReminderTable.ReadRows(currentPointer, currentPointer + _options.RemindersMigrationBatchSize);
                    _logger.Info($"Fetched batch: {entries.Reminders.Count} reminders");
                    if (entries.Reminders.Count == 0)
                    {
                        break;
                    }

                    foreach (var entry in entries.Reminders)
                    {
                        try
                        {
                            await _reminderMigrationStorage.DestinationReminderTable.UpsertRow(entry);
                            migrationStats.MigratedEntries++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Exception occurred during reminder '{entry.ReminderName}' migration");
                            migrationStats.FailedEntries++;
                        }
                        
                    }

                    currentPointer += _options.RemindersMigrationBatchSize;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception occurred during reminders batch processing");
                    migrationStats.FailedEntries += _options.RemindersMigrationBatchSize;
                }
            }

            _logger.Info("Finished reminders migration");
            return migrationStats;
        }

        public class MigrationStats
        {
            public uint SkippedEntries { get; internal set; }
            public uint MigratedEntries { get; internal set; }
            public uint FailedEntries { get; internal set; }
        }

        public class Options
        {
            public bool DontSkipMigrateEntries { get; set; } = false;
            public uint RemindersMigrationBatchSize { get; set; } = 10000;
        }
    }
}
