using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.AzureUtils.Utilities;
using Orleans.Configuration;
using Orleans.Reminders.AzureStorage;

namespace Orleans.Runtime.ReminderService
{
    /// <summary>
    /// Stores and retrieves Orleans reminder entries using Azure Table Storage.
    /// </summary>
    public sealed partial class AzureBasedReminderTable : IReminderTable
    {
        private readonly ILogger logger;
        private readonly ILoggerFactory loggerFactory;
        private readonly ClusterOptions clusterOptions;
        private readonly AzureTableReminderStorageOptions storageOptions;
        private readonly RemindersTableManager remTableManager;
        private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
        private TaskCompletionSource _initializationTask = CreateInitializationSource();

        /// <summary>
        /// Initializes a new instance of the <see cref="AzureBasedReminderTable"/> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory.</param>
        /// <param name="clusterOptions">The cluster identity options used to scope reminder entries.</param>
        /// <param name="storageOptions">The Azure Table Storage reminder options.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="clusterOptions"/> or <paramref name="storageOptions"/> is <see langword="null"/>.
        /// </exception>
        public AzureBasedReminderTable(
            ILoggerFactory loggerFactory,
            IOptions<ClusterOptions> clusterOptions,
            IOptions<AzureTableReminderStorageOptions> storageOptions)
        {
            this.logger = loggerFactory.CreateLogger<AzureBasedReminderTable>();
            this.loggerFactory = loggerFactory;
            ArgumentNullException.ThrowIfNull(clusterOptions);
            ArgumentNullException.ThrowIfNull(storageOptions);
            this.clusterOptions = clusterOptions.Value;
            this.storageOptions = storageOptions.Value;
            this.remTableManager = new RemindersTableManager(
                this.clusterOptions.ServiceId,
                this.clusterOptions.ClusterId,
                this.storageOptions,
                this.loggerFactory);
        }

        /// <summary>
        /// Connects to Azure Table Storage and creates the reminder table if it does not exist.
        /// </summary>
        /// <param name="cancellationToken">The token used to cancel lifecycle-lock acquisition and delays between initialization attempts.</param>
        /// <returns>A task representing the initialization operation.</returns>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _lifecycleLock.WaitAsync(cancellationToken);
            try
            {
                if (_initializationTask.Task.IsCompletedSuccessfully)
                {
                    return;
                }

                if (_initializationTask.Task.IsCompleted)
                {
                    Volatile.Write(ref _initializationTask, CreateInitializationSource());
                }

                var initialization = _initializationTask;
                while (true)
                {
                    try
                    {
                        await remTableManager.InitTableAsync(cancellationToken);
                        initialization.TrySetResult();
                        return;
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        LogErrorCreatingAzureTable(ex);
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException ex)
            {
                LogErrorReminderTableInitializationCanceled(ex);
                _initializationTask.TrySetCanceled(ex.CancellationToken);
                throw;
            }
            catch (Exception ex)
            {
                LogErrorInitializingReminderTable(ex);
                _initializationTask.TrySetException(ex);
                throw;
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        /// <summary>
        /// Stops the reminder table until it is started again.
        /// </summary>
        /// <param name="cancellationToken">The token used to cancel the stop operation.</param>
        /// <returns>A task representing the stop operation.</returns>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _lifecycleLock.WaitAsync(cancellationToken);
            try
            {
                var stopped = CreateInitializationSource();
                stopped.TrySetCanceled(CancellationToken.None);
                Volatile.Write(ref _initializationTask, stopped);
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        private static TaskCompletionSource CreateInitializationSource()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private ReminderTableData ConvertFromTableEntryList(List<(ReminderTableEntry Entity, string ETag)> entries)
        {
            var remEntries = new List<ReminderEntry>();
            foreach (var entry in entries)
            {
#pragma warning disable RCS1075 // Avoid empty catch clause that catches System.Exception.
                try
                {
                    ReminderEntry converted = ConvertFromTableEntry(entry.Entity, entry.ETag);
                    remEntries.Add(converted);
                }
                catch (Exception)
                {
                    // Ignoring...
                }
#pragma warning restore RCS1075 // Avoid empty catch clause that catches System.Exception.
            }
            return new ReminderTableData(remEntries);
        }

        private ReminderEntry ConvertFromTableEntry(ReminderTableEntry tableEntry, string eTag)
        {
            try
            {
                return new ReminderEntry
                {
                    GrainId = GrainId.Parse(tableEntry.GrainReference!),
                    ReminderName = tableEntry.ReminderName!,
                    StartAt = LogFormatter.ParseDate(tableEntry.StartAt!),
                    Period = TimeSpan.Parse(tableEntry.Period!),
                    ETag = eTag,
                };
            }
            catch (Exception exc)
            {
                LogErrorParsingReminderEntry(exc, tableEntry);
                throw;
            }
            finally
            {
                string serviceIdStr = this.clusterOptions.ServiceId;
                if (!tableEntry.ServiceId!.Equals(serviceIdStr, StringComparison.Ordinal))
                {
                    LogWarningAzureTable_ReadWrongReminder(tableEntry, serviceIdStr);
                    throw new OrleansException($"Read a reminder entry for wrong Service id. Read {tableEntry}, but my service id is {serviceIdStr}. Going to discard it.");
                }
            }
        }

        private static ReminderTableEntry ConvertToTableEntry(ReminderEntry remEntry, string serviceId, string deploymentId)
        {
            string partitionKey = ReminderTableEntry.ConstructPartitionKey(serviceId, remEntry.GrainId);
            string rowKey = ReminderTableEntry.ConstructRowKey(remEntry.GrainId, remEntry.ReminderName);

            var consistentHash = remEntry.GrainId.GetUniformHashCode();

            return new ReminderTableEntry
            {
                PartitionKey = partitionKey,
                RowKey = rowKey,

                ServiceId = serviceId,
                DeploymentId = deploymentId,
                GrainReference = remEntry.GrainId.ToString(),
                ReminderName = remEntry.ReminderName,

                StartAt = LogFormatter.PrintDate(remEntry.StartAt),
                Period = remEntry.Period.ToString(),

                GrainRefConsistentHash = consistentHash.ToString("X8"),
                // The Azure SDK accepts the default reminder ETag even though its string constructor is non-nullable.
                ETag = new ETag(remEntry.ETag!),
            };
        }

        /// <summary>
        /// Deletes all reminder entries for the current Orleans service and cluster.
        /// </summary>
        /// <returns>A task representing the delete operation.</returns>
        public async Task TestOnlyClearTable()
        {
            await Volatile.Read(ref _initializationTask).Task;

            await this.remTableManager.DeleteTableEntries();
        }

        /// <summary>
        /// Reads all reminder entries associated with the specified grain.
        /// </summary>
        /// <param name="grainId">The grain identifier.</param>
        /// <returns>The reminder entries associated with <paramref name="grainId"/>.</returns>
        public async Task<ReminderTableData> ReadRows(GrainId grainId)
        {
            try
            {
                await Volatile.Read(ref _initializationTask).Task;

                var entries = await this.remTableManager.FindReminderEntries(grainId);
                ReminderTableData data = ConvertFromTableEntryList(entries);
                LogTraceReadForGrain(grainId, data);
                return data;
            }
            catch (Exception exc)
            {
                LogWarningReadingReminders(exc, grainId, this.remTableManager.TableName);
                throw;
            }
        }

        /// <summary>
        /// Reads reminder entries whose grain hash is in the range <c>(begin, end]</c>.
        /// </summary>
        /// <param name="begin">The exclusive lower bound of the hash range.</param>
        /// <param name="end">The inclusive upper bound of the hash range.</param>
        /// <returns>The reminder entries in the specified hash range.</returns>
        public async Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            try
            {
                await Volatile.Read(ref _initializationTask).Task;

                var entries = await this.remTableManager.FindReminderEntries(begin, end);
                ReminderTableData data = ConvertFromTableEntryList(entries);
                LogTraceReadInRange(new(begin, end), data);
                return data;
            }
            catch (Exception exc)
            {
                LogWarningReadingReminderRange(exc, new(begin, end), this.remTableManager.TableName);
                throw;
            }
        }

        /// <summary>
        /// Reads a reminder entry for the specified grain and reminder name.
        /// </summary>
        /// <param name="grainId">The grain identifier.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <returns>The reminder entry when found; otherwise, <see langword="null"/>.</returns>
        public async Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
        {
            try
            {
                await Volatile.Read(ref _initializationTask).Task;

                LogDebugReadRow(grainId, reminderName);
                var result = await this.remTableManager.FindReminderEntry(grainId, reminderName);
                return result.Entity is null ? null : ConvertFromTableEntry(result.Entity, result.ETag!);
            }
            catch (Exception exc)
            {
                LogWarningReadingReminderRow(exc, grainId, reminderName, this.remTableManager.TableName);
                throw;
            }
        }

        /// <summary>
        /// Inserts or replaces a reminder entry.
        /// </summary>
        /// <param name="entry">The reminder entry to store.</param>
        /// <returns>The new entity tag when the write succeeds; otherwise, <see langword="null"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
        public async Task<string?> UpsertRow(ReminderEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            try
            {
                await Volatile.Read(ref _initializationTask).Task;

                LogDebugUpsertRow(entry);
                ReminderTableEntry remTableEntry = ConvertToTableEntry(entry, this.clusterOptions.ServiceId, this.clusterOptions.ClusterId);

                string? result = await this.remTableManager.UpsertRow(remTableEntry);
                if (result == null)
                {
                    LogWarningReminderUpsertFailed(entry);
                }
                return result;
            }
            catch (Exception exc)
            {
                LogWarningUpsertReminderEntry(exc, entry, this.remTableManager.TableName);
                throw;
            }
        }

        /// <summary>
        /// Removes a reminder entry when its entity tag matches the stored entry.
        /// </summary>
        /// <param name="grainId">The grain identifier.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <param name="eTag">The entity tag used for optimistic concurrency.</param>
        /// <returns>
        /// <see langword="true"/> when the entry was removed; otherwise, <see langword="false"/>.
        /// </returns>
        public async Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
        {
            var entry = new ReminderTableEntry
            {
                PartitionKey = ReminderTableEntry.ConstructPartitionKey(this.clusterOptions.ServiceId, grainId),
                RowKey = ReminderTableEntry.ConstructRowKey(grainId, reminderName),
                ETag = new ETag(eTag),
            };

            try
            {
                await Volatile.Read(ref _initializationTask).Task;

                LogTraceRemoveRow(entry);

                bool result = await this.remTableManager.DeleteReminderEntryConditionally(entry, eTag);
                if (result == false)
                {
                    LogWarningOnReminderDeleteRetry(entry);
                }
                return result;
            }
            catch (Exception exc)
            {
                LogWarningWhenDeletingReminder(exc, entry, this.remTableManager.TableName);
                throw;
            }
        }

        private readonly struct RingRangeLogValue(uint Begin, uint End)
        {
            public override string? ToString() => RangeFactory.CreateRange(Begin, End).ToString();
        }

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)AzureReminderErrorCode.AzureTable_39,
            Message = "Exception trying to create or connect to the Azure table"
        )]
        private partial void LogErrorCreatingAzureTable(Exception ex);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Reminder table initialization canceled."
        )]
        private partial void LogErrorReminderTableInitializationCanceled(Exception ex);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Error initializing reminder table."
        )]
        private partial void LogErrorInitializingReminderTable(Exception ex);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)AzureReminderErrorCode.AzureTable_49,
            Message = "Failed to parse ReminderTableEntry: {TableEntry}. This entry is corrupt, going to ignore it."
        )]
        private partial void LogErrorParsingReminderEntry(Exception ex, object tableEntry);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)AzureReminderErrorCode.AzureTable_ReadWrongReminder,
            Message = "Read a reminder entry for wrong Service id. Read {TableEntry}, but my service id is {ServiceId}. Going to discard it."
        )]
        private partial void LogWarningAzureTable_ReadWrongReminder(ReminderTableEntry tableEntry, string serviceId);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Read for grain {GrainId} Table={Data}"
        )]
        private partial void LogTraceReadForGrain(GrainId grainId, ReminderTableData data);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)AzureReminderErrorCode.AzureTable_47,
            Message = "Intermediate error reading reminders for grain {GrainId} in table {TableName}."
        )]
        private partial void LogWarningReadingReminders(Exception ex, GrainId grainId, string tableName);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Read in {RingRange} Table={Data}"
        )]
        private partial void LogTraceReadInRange(RingRangeLogValue ringRange, ReminderTableData data);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)AzureReminderErrorCode.AzureTable_40,
            Message = "Intermediate error reading reminders in range {RingRange} for table {TableName}."
        )]
        private partial void LogWarningReadingReminderRange(Exception ex, RingRangeLogValue ringRange, string tableName);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "ReadRow grainRef = {GrainId} reminderName = {ReminderName}"
        )]
        private partial void LogDebugReadRow(GrainId grainId, string reminderName);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)AzureReminderErrorCode.AzureTable_46,
            Message = "Intermediate error reading row with grainId = {GrainId} reminderName = {ReminderName} from table {TableName}."
        )]
        private partial void LogWarningReadingReminderRow(Exception ex, GrainId grainId, string reminderName, string tableName);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "UpsertRow entry = {Data}"
        )]
        private partial void LogDebugUpsertRow(ReminderEntry data);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)AzureReminderErrorCode.AzureTable_45,
            Message = "Upsert failed on the reminder table. Will retry. Entry = {Data}"
        )]
        private partial void LogWarningReminderUpsertFailed(ReminderEntry data);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)AzureReminderErrorCode.AzureTable_42,
            Message = "Intermediate error upserting reminder entry {Data} to the table {TableName}."
        )]
        private partial void LogWarningUpsertReminderEntry(Exception ex, ReminderEntry data, string tableName);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "RemoveRow entry = {Data}"
        )]
        private partial void LogTraceRemoveRow(ReminderTableEntry data);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)AzureReminderErrorCode.AzureTable_43,
            Message = "Delete failed on the reminder table. Will retry. Entry = {Data}"
        )]
        private partial void LogWarningOnReminderDeleteRetry(ReminderTableEntry data);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)AzureReminderErrorCode.AzureTable_44,
            Message = "Intermediate error when deleting reminder entry {Data} to the table {TableName}."
        )]
        private partial void LogWarningWhenDeletingReminder(Exception ex, ReminderTableEntry data, string tableName);
    }
}
