using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Persistence.Migration
{
    public class OfflineMigrator
    {
        private readonly ILogger<OfflineMigrator> _logger;
        private readonly Options _options;

        private readonly IGrainStorage _oldStorage;
        private readonly IGrainStorage _newStorage;

        public OfflineMigrator(
            ILogger<OfflineMigrator> logger,
            IGrainStorage oldStorage,
            IGrainStorage newStorage,
            Options options)
        {
            _logger = logger;
            _options = options ?? new Options();

            _oldStorage = oldStorage;
            _newStorage = newStorage;
        }

        /// <summary>
        /// Careful: is a long-time running operation.<br/>
        /// Goes through all the data items in the old storage and migrates them in a new format to the new storage.
        /// </summary>
        public async Task<MigrationStats> MigrateAsync(CancellationToken cancellationToken)
        {
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
        }
    }
}
