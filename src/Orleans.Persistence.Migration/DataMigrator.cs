using Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Persistence.Migration
{
    public class DataMigrator : IHostedService
    {
        private readonly ILogger<DataMigrator> _logger;
        private readonly DataMigratorOptions _options;

        private readonly IClusterMembershipService _clusterMembershipService;
        private readonly ILocalSiloDetails _localSiloDetails;

        private readonly IExtendedGrainStorage _sourceStorage;
        private readonly IGrainStorage _destinationStorage;
        private readonly IReminderMigrationTable _reminderMigrationStorage;

        private readonly CancellationTokenSource _backgroundWorkCts = new();
        private object _lastProcessedGrainCursor;

        public DataMigrator(
            ILogger<DataMigrator> logger,
            IClusterMembershipService clusterMembershipService,
            ILocalSiloDetails localSiloDetails,
            IGrainStorage sourceStorage,
            IGrainStorage destinationStorage,
            IReminderMigrationTable reminderMigrationTable,
            DataMigratorOptions options)
        {
            _logger = logger;
            _options = options ?? new();

            _clusterMembershipService = clusterMembershipService;
            _localSiloDetails = localSiloDetails;

            // instead of doing re-registrations of same storage, we can just check if it's already IGrainStorageEntriesController
            // if not - we simply fail fast with an explicit error message 
            _sourceStorage = (sourceStorage is IExtendedGrainStorage oldStorageEntriesController)
                ? oldStorageEntriesController
                : throw new ArgumentException($"Implement {nameof(IExtendedGrainStorage)} on grainStorage to support data migration.", paramName: nameof(sourceStorage));
            _destinationStorage = destinationStorage;

            _reminderMigrationStorage = reminderMigrationTable;
        }

        public Task StartAsync(CancellationToken cancellationToken) => ExecuteBackgroundMigrationAsync(_backgroundWorkCts.Token);
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _backgroundWorkCts.Cancel();
            return Task.CompletedTask;
        }

        private async Task ExecuteBackgroundMigrationAsync(CancellationToken stoppingToken)
        {
            if (_options.BackgroundTaskInitialDelay.HasValue)
            {
                await Task.Delay(_options.BackgroundTaskInitialDelay.Value, stoppingToken);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _clusterMembershipService.Refresh();
                    var firstAddressSilo = _clusterMembershipService.CurrentSnapshot.Members.Values
                        .Where(s => s.Status == SiloStatus.Active)
                        .OrderBy(s => s.SiloAddress)
                        .FirstOrDefault();

                    if (firstAddressSilo is null)
                    {
                        _logger.Info("No silos available, retrying in 15 sec...");
                        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                        continue;
                    }

                    if (!firstAddressSilo.SiloAddress.Equals(_localSiloDetails.SiloAddress))
                    {
                        // DataMigrator should run only from the "primary" silo (can be changed later after cluster updates),
                        // So we can await and retry here
                        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                        continue;
                    }

                    // current silo is primary. Starting work here
                    var migrateGrainsTask = MigrateGrainsAsync(stoppingToken);
                    var migrateRemindersTask = MigrateRemindersAsync(stoppingToken);
                    await Task.WhenAll(migrateGrainsTask, migrateRemindersTask);

                    var grainMigrationResult = await migrateGrainsTask;
                    var reminderMigrationResult = await migrateRemindersTask;

                    if (grainMigrationResult.EntriesMigratedOrSkipped)
                    {
                        _logger.Info("Successfully migrated all grains!");
                    }

                    if (!reminderMigrationResult.IsAvailable)
                    {
                        _logger.Info("Reminder migration is not available. " + reminderMigrationResult.Reason);
                    }
                    else if (reminderMigrationResult.EntriesMigratedOrSkipped)
                    {
                        _logger.Info("Successfully migrated all reminders!");
                    }

                    if (grainMigrationResult.EntriesMigratedOrSkipped && reminderMigrationResult.EntriesMigratedOrSkipped)
                    {
                        _logger.Info("Migration completed");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error((int)MigrationErrorCodes.DataMigrationBackgroundTaskFailed, $"Failed to run {nameof(DataMigrator)} background work. Retrying in 2 seconds...", ex);
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
            }
        }

        /// <summary>
        /// Careful: is a long-time running operation.<br/>
        /// Goes through all the data items in the old storage and migrates them in a new format to the new storage.
        /// </summary>
        public async Task<MigrationStats> MigrateGrainsAsync(CancellationToken cancellationToken)
        {
            _logger.Info("Starting grains migration. LastProcessedGrainCursor: {cursor}", _lastProcessedGrainCursor);

            var migrationStats = new MigrationStats();
            await foreach (var storageEntry in _sourceStorage.GetAll(_lastProcessedGrainCursor, cancellationToken))
            {
                var initGrainType = storageEntry.GrainType;
                migrationStats.EntriesProcessed++;
                if (!_options.DontSkipMigrateEntries)
                {
                    IGrainState tmpState = new GrainState<object>();
                    await _destinationStorage.ReadStateAsync(storageEntry.GrainType, storageEntry.GrainReference, tmpState);

                    if (tmpState is not null && tmpState.RecordExists)
                    {
                        _logger.Info("Entry (type='{grainType}';ref='{reference}') already exists at destination storage", storageEntry.GrainType, storageEntry.GrainReference.ToShortKeyString());
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
                    _lastProcessedGrainCursor = storageEntry.Cursor;
                    _logger.Debug("Grain {grainType} with key {grainKey} is migrated successfully. StorageEntry: {storageEntry}", storageEntry.GrainType, storageEntry.GrainReference.ToShortKeyString(), _lastProcessedGrainCursor);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error migrating grain {GrainType} with reference key='{GrainReference}'", storageEntry.GrainType, storageEntry.GrainReference.ToShortKeyString());
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
                return new MigrationStats
                {
                    IsAvailable = false,
                    Reason = "Migration reminder storage is not available. Use 'UseMigrationAzureTableReminderStorage()' to register Reminder's migration component."
                };
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
                        migrationStats.EntriesProcessed++;
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
            /// <summary>
            /// If migration is not available, will be false.
            /// See <see cref="Reason"/> for reason.
            /// </summary>
            public bool IsAvailable { get; internal set; } = true;
            /// <summary>
            /// If migration is not available, will contain reason details.
            /// </summary>
            public string Reason { get; internal set; }

            /// <summary>
            /// If all entries were skipped on the query level
            /// </summary>
            public bool SkippedAllEntries => EntriesProcessed == 0;

            public uint SkippedEntries { get; internal set; }
            public uint MigratedEntries { get; internal set; }
            public uint FailedEntries { get; internal set; }
            public uint EntriesProcessed { get; internal set; }

            public bool EntriesMigratedOrSkipped
                => SkippedEntries + MigratedEntries == EntriesProcessed
                    && FailedEntries == 0;
        }
    }

    public class DataMigratorOptions
    {
        public bool DontSkipMigrateEntries { get; set; } = false;
        public uint RemindersMigrationBatchSize { get; set; } = 10000;

        /// <summary>
        /// Time to await after app startup before running <see cref="DataMigrator.ExecuteBackgroundMigrationAsync(CancellationToken)"/>.
        /// If you want to skip awaiting, set it to null.
        /// </summary>
        public TimeSpan? BackgroundTaskInitialDelay { get; set; } = TimeSpan.FromMinutes(2);
    }
}
