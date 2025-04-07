using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Persistence.AzureStorage.Migration.Reminders.Storage;
using Orleans.Persistence.Migration;
using Orleans.Reminders.AzureStorage;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;

namespace Orleans.Persistence.AzureStorage.Migration.Reminders
{
    /// <summary>
    /// Simple storage provider for writing grain state data to Azure blob storage in JSON format.
    /// Implementation impacts management of migrated and current data.
    /// </summary>
    public class MigrationAzureTableReminderStorage : IReminderMigrationTable
    {
        private readonly ILogger<MigrationAzureTableReminderStorage> _logger;

        public IReminderTable SourceReminderTable { get; }
        public IReminderTable DestinationReminderTable { get; }

        private ReminderMigrationMode _reminderMigrationMode;

        public MigrationAzureTableReminderStorage(
            IGrainReferenceConverter grainReferenceConverter,
            ILoggerFactory loggerFactory,
            IGrainReferenceExtractor grainReferenceExtractor,
            IOptions<ClusterOptions> clusterOptions,
            IOptions<AzureTableReminderStorageOptions> oldStorageOptions,
            IOptions<AzureTableMigrationReminderStorageOptions> migratedStorageOptions)
        {
            _logger = loggerFactory.CreateLogger<MigrationAzureTableReminderStorage>();

            SourceReminderTable = new AzureBasedReminderTable(grainReferenceConverter, loggerFactory, clusterOptions, oldStorageOptions);
            DestinationReminderTable = new MigrationAzureBasedReminderTable(grainReferenceConverter, grainReferenceExtractor, loggerFactory, clusterOptions, migratedStorageOptions);

            if (migratedStorageOptions?.Value is not null)
            {
                _reminderMigrationMode = migratedStorageOptions.Value.ReminderMigrationMode;
            }
        }

        /// <summary>
        /// Completely disables migration tooling to use only the source storage
        /// </summary>
        public void DisableMigrationTooling()
        {
            _reminderMigrationMode = ReminderMigrationMode.Disabled;
        }

        /// <summary>
        /// Changes mode to specified one
        /// </summary>
        public void ChangeMode(ReminderMigrationMode mode)
        {
            _reminderMigrationMode = mode;
        }

        public async Task Init()
        {
            switch (_reminderMigrationMode)
            {
                case ReminderMigrationMode.Disabled:
                    await SourceReminderTable.Init();
                    break;

                case ReminderMigrationMode.ReadSource_WriteBoth:
                case ReminderMigrationMode.ReadDestinationWithFallback_WriteBoth:
                case ReminderMigrationMode.ReadDestinationWithFallback_WriteDestination:
                    await SourceReminderTable.Init();
                    await DestinationReminderTable.Init();
                    break;

                case ReminderMigrationMode.ReadWriteDestination:
                    await DestinationReminderTable.Init();
                    break;

                default: throw new ArgumentOutOfRangeException(nameof(_reminderMigrationMode));
            }
        }

        public async Task<string> UpsertRow(ReminderEntry entry)
        {
            switch (_reminderMigrationMode)
            {
                case ReminderMigrationMode.Disabled:
                    return await SourceReminderTable.UpsertRow(entry);

                case ReminderMigrationMode.ReadSource_WriteBoth:
                {
                    await DestinationReminderTable.UpsertRow(entry);
                    return await SourceReminderTable.UpsertRow(entry);
                }

                case ReminderMigrationMode.ReadDestinationWithFallback_WriteBoth:
                case ReminderMigrationMode.ReadDestinationWithFallback_WriteDestination:
                {
                    var destinationResult = await DestinationReminderTable.UpsertRow(entry);
                    await SourceReminderTable.UpsertRow(entry);
                    return destinationResult;
                }

                case ReminderMigrationMode.ReadWriteDestination:
                    return await DestinationReminderTable.UpsertRow(entry);

                default: throw new ArgumentOutOfRangeException(nameof(_reminderMigrationMode));
            }
        }

        public async Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag)
        {
            switch (_reminderMigrationMode)
            {
                case ReminderMigrationMode.Disabled:
                    return await SourceReminderTable.RemoveRow(grainRef, reminderName, eTag);

                case ReminderMigrationMode.ReadSource_WriteBoth:
                {
                    await DestinationReminderTable.RemoveRow(grainRef, reminderName, eTag);
                    return await SourceReminderTable.RemoveRow(grainRef, reminderName, eTag);
                }

                case ReminderMigrationMode.ReadDestinationWithFallback_WriteBoth:
                case ReminderMigrationMode.ReadDestinationWithFallback_WriteDestination:
                {
                    var destinationResult = await DestinationReminderTable.RemoveRow(grainRef, reminderName, eTag);
                    await SourceReminderTable.RemoveRow(grainRef, reminderName, eTag);
                    return destinationResult;
                }

                case ReminderMigrationMode.ReadWriteDestination:
                    return await DestinationReminderTable.RemoveRow(grainRef, reminderName, eTag);

                default: throw new ArgumentOutOfRangeException(nameof(_reminderMigrationMode));
            }
        }

        public async Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName)
        {
            switch (_reminderMigrationMode)
            {
                case ReminderMigrationMode.Disabled:
                case ReminderMigrationMode.ReadSource_WriteBoth:
                    return await SourceReminderTable.ReadRow(grainRef, reminderName);

                case ReminderMigrationMode.ReadDestinationWithFallback_WriteBoth:
                case ReminderMigrationMode.ReadDestinationWithFallback_WriteDestination:
                {
                    var entry = await DestinationReminderTable.ReadRow(grainRef, reminderName);
                    if (entry is not null)
                    {
                        return entry;
                    }
                    else
                    {
                        return await SourceReminderTable.ReadRow(grainRef, reminderName);
                    }
                }

                case ReminderMigrationMode.ReadWriteDestination:
                    return await DestinationReminderTable.ReadRow(grainRef, reminderName);

                default: throw new ArgumentOutOfRangeException(nameof(_reminderMigrationMode));
            }
        }

        public async Task<ReminderTableData> ReadRows(GrainReference key)
        {
            switch (_reminderMigrationMode)
            {
                case ReminderMigrationMode.Disabled:
                case ReminderMigrationMode.ReadSource_WriteBoth:
                    return await SourceReminderTable.ReadRows(key);

                case ReminderMigrationMode.ReadDestinationWithFallback_WriteBoth:
                case ReminderMigrationMode.ReadDestinationWithFallback_WriteDestination:
                {
                    var entry = await DestinationReminderTable.ReadRows(key);
                    if (entry is not null)
                    {
                        return entry;
                    }
                    else
                    {
                        return await SourceReminderTable.ReadRows(key);
                    }
                }

                case ReminderMigrationMode.ReadWriteDestination:
                    return await DestinationReminderTable.ReadRows(key);

                default: throw new ArgumentOutOfRangeException(nameof(_reminderMigrationMode));
            }
        }

        public async Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            switch (_reminderMigrationMode)
            {
                case ReminderMigrationMode.Disabled:
                case ReminderMigrationMode.ReadSource_WriteBoth:
                    return await SourceReminderTable.ReadRows(begin, end);

                case ReminderMigrationMode.ReadDestinationWithFallback_WriteBoth:
                case ReminderMigrationMode.ReadDestinationWithFallback_WriteDestination:
                {
                    var entry = await DestinationReminderTable.ReadRows(begin, end);
                    if (entry is not null)
                    {
                        return entry;
                    }
                    else
                    {
                        return await SourceReminderTable.ReadRows(begin, end);
                    }
                }

                case ReminderMigrationMode.ReadWriteDestination:
                    return await DestinationReminderTable.ReadRows(begin, end);

                default: throw new ArgumentOutOfRangeException(nameof(_reminderMigrationMode));
            }
        }

        public async Task TestOnlyClearTable()
        {
            switch (_reminderMigrationMode)
            {
                case ReminderMigrationMode.Disabled:
                    await SourceReminderTable.TestOnlyClearTable();
                    break;

                case ReminderMigrationMode.ReadSource_WriteBoth:
                case ReminderMigrationMode.ReadDestinationWithFallback_WriteBoth:
                case ReminderMigrationMode.ReadDestinationWithFallback_WriteDestination:
                    await SourceReminderTable.TestOnlyClearTable();
                    await DestinationReminderTable.TestOnlyClearTable();
                    break;

                case ReminderMigrationMode.ReadWriteDestination:
                    await DestinationReminderTable.TestOnlyClearTable();
                    break;

                default: throw new ArgumentOutOfRangeException(nameof(_reminderMigrationMode));
            }
        }

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

    /// <summary>
    /// Controls the reminder migration mode of the underlying reminder storage
    /// </summary>
    public enum ReminderMigrationMode
    {
        /// <summary>
        /// Reminder migration is completely disabled.
        /// Only source storage will be used for read/write and clear operations.
        /// </summary>
        Disabled = 0,

        /// <summary>
        /// Reading would happen from source storage only, and writes will target both source and destination storages.
        /// <br/>
        /// <i>Should be used as a first step of migration.</i>
        /// </summary>
        ReadSource_WriteBoth = 1,

        /// <summary>
        /// Reading would happen from destination storage, and if entry does not exist, will also check source storage.
        /// Write will target both storages.
        /// <br/>
        /// <i>Should be used as latter step of migration.</i>
        /// </summary>
        ReadDestinationWithFallback_WriteBoth = 2,

        /// <summary>
        /// Reading would happen from destination storage, and if entry does not exist, will also check source storage.
        /// Write will target only destination.
        /// <br/>
        /// <i>Should be used as latter step of migration.</i>
        /// </summary>
        ReadDestinationWithFallback_WriteDestination = 3,

        /// <summary>
        /// Reading and writing happens only against destination storage
        /// </summary>
        ReadWriteDestination = 4
    }
}
