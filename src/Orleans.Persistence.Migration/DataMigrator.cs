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

        private Task _executeBackgroundMigrationTask;
        private readonly CancellationTokenSource _backgroundWorkCts = new();
        private object _lastProcessedGrainCursor;

        public DataMigrator(
            ILogger<DataMigrator> logger,
            IClusterMembershipService clusterMembershipService,
            ILocalSiloDetails localSiloDetails,
            IGrainStorage sourceStorage,
            IGrainStorage destinationStorage,
            IReminderTable reminderTable,
            DataMigratorOptions options)
        {
            _logger = logger;
            _options = options ?? new();

            _clusterMembershipService = clusterMembershipService;
            _localSiloDetails = localSiloDetails;

            // instead of doing re-registrations of same storage, we can just check if it's already IExtendedGrainStorage
            // if not - we simply fail fast with an explicit error message 
            _sourceStorage = (sourceStorage is IExtendedGrainStorage oldStorageEntriesController)
                ? oldStorageEntriesController
                : throw new ArgumentException($"Implement {nameof(IExtendedGrainStorage)} on grain storage to support data migration.", paramName: nameof(sourceStorage));
            _destinationStorage = destinationStorage;

            _reminderMigrationStorage = (reminderTable is IReminderMigrationTable reminderMigrationTable)
                ? reminderMigrationTable
                : null!;
        }

        public Task StartAsync(CancellationToken cancellationToken) => _executeBackgroundMigrationTask = ExecuteBackgroundMigrationAsync(_backgroundWorkCts.Token);
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _backgroundWorkCts.Cancel();
            await _executeBackgroundMigrationTask;
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
                    var firstAddressSilo = _clusterMembershipService.CurrentSnapshot.Members.Values
                        .Where(s => s.Status == SiloStatus.Active)
                        .OrderBy(s => s.SiloAddress)
                        .FirstOrDefault();

                    if (firstAddressSilo is null)
                    {
                        _logger.LogInformation("No silos available, retrying in 15 sec...");
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
                        _logger.LogInformation("Successfully migrated all grains!");
                    }

                    if (!reminderMigrationResult.IsAvailable)
                    {
                        _logger.LogInformation("Reminder migration is not available. " + reminderMigrationResult.Reason);
                    }
                    else if (reminderMigrationResult.EntriesMigratedOrSkipped)
                    {
                        _logger.LogInformation("Successfully migrated all reminders!");
                    }

                    if (grainMigrationResult.EntriesMigratedOrSkipped && reminderMigrationResult.EntriesMigratedOrSkipped)
                    {
                        _logger.LogInformation("Migration completed");
                        return;
                    }

                    if (grainMigrationResult.SkippedAllEntries)
                    {
                        _logger.LogInformation("Migration completed");
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
            _logger.LogInformation("Starting grains migration. LastProcessedGrainCursor: {cursor}", _lastProcessedGrainCursor);

            var migrationStats = new MigrationStats();
            await foreach (var storageEntry in _sourceStorage.GetAll(_lastProcessedGrainCursor, cancellationToken))
            {
                migrationStats.EntriesProcessed++;
                if (!_options.ProcessMigratedEntries)
                {
                    IGrainState tmpState = new GrainState<object>();
                    await _destinationStorage.ReadStateAsync(storageEntry.GrainType, storageEntry.GrainReference, tmpState);

                    if (tmpState is not null && tmpState.RecordExists)
                    {
                        _logger.LogInformation("Entry (type='{grainType}';ref='{reference}') already exists at destination storage", storageEntry.GrainType, storageEntry.GrainReference.ToShortKeyString());
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
                        _logger.LogInformation("Migrated blob already exists, but was not skipped: {entryName};", storageEntry.GrainType);
                        // ignore: we have already migrated this entry to new storage.
                    }
                    catch (InconsistentStateException ex) when (ex.Message.Contains("Resource with specified id or name already exists"))
                    {
                        _logger.LogInformation("Migrated cosmosDb doc already exists, but was not skipped: {entryName};", storageEntry.GrainType);
                        // ignore: we have already migrated this entry to new storage.
                    }

                    migrationStats.MigratedEntries++;
                    _lastProcessedGrainCursor = storageEntry.Cursor;
                    _logger.LogDebug("Grain {grainType} with key {grainKey} is migrated successfully. StorageEntry: {storageEntry}", storageEntry.GrainType, storageEntry.GrainReference.ToShortKeyString(), _lastProcessedGrainCursor);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error migrating grain {GrainType} with reference key='{GrainReference}'", storageEntry.GrainType, storageEntry.GrainReference.ToShortKeyString());
                    migrationStats.FailedEntries++;
                }
            }

            _logger.LogInformation("Finished grains migration");
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

            _logger.LogInformation("Starting reminders migration");
            var migrationStats = new MigrationStats();

            uint currentPointer = startingGrainRefHashCode;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var entries = await _reminderMigrationStorage.SourceReminderTable.ReadRows(currentPointer, currentPointer + _options.RemindersMigrationBatchSize);
                    _logger.LogInformation($"Fetched batch: {entries.Reminders.Count} reminders");
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

            _logger.LogInformation("Finished reminders migration");
            return migrationStats;
        }

        /// <summary>
        /// Represents migration statistics for a single migration operation.
        /// </summary>
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

    /// <summary>
    /// Options for data migration, including whether to process already migrated entries, batch size for reminders, and
    /// initial delay for background tasks.
    /// </summary>
    public class DataMigratorOptions
    {
        /// <summary>
        /// If false, will lookup to destination storage to identify whether entry was already migrated.
        /// If true, will forcefully migrate the entry.
        /// </summary>
        public bool ProcessMigratedEntries { get; set; } = false;

        /// <summary>
        /// Batch size of how many reminder entries should be taken at a single query to underlying storage
        /// </summary>
        public uint RemindersMigrationBatchSize { get; set; } = 10000;

        /// <summary>
        /// Time to await after app startup before running <see cref="DataMigrator.ExecuteBackgroundMigrationAsync(CancellationToken)"/>.
        /// If you want to skip awaiting, set it to null.
        /// </summary>
        public TimeSpan? BackgroundTaskInitialDelay { get; set; } = TimeSpan.FromMinutes(2);
    }
}
