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
    public sealed partial class AzureBasedReminderTable : IReminderTable
    {
        private readonly ILogger logger;
        private readonly ILoggerFactory loggerFactory;
        private readonly ClusterOptions clusterOptions;
        private readonly AzureTableReminderStorageOptions storageOptions;
        private readonly RemindersTableManager remTableManager;
        private readonly TaskCompletionSource _initializationTask = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AzureBasedReminderTable(
            ILoggerFactory loggerFactory,
            IOptions<ClusterOptions> clusterOptions,
            IOptions<AzureTableReminderStorageOptions> storageOptions)
        {
            this.logger = loggerFactory.CreateLogger<AzureBasedReminderTable>();
            this.loggerFactory = loggerFactory;
            this.clusterOptions = clusterOptions.Value;
            this.storageOptions = storageOptions.Value;
            this.remTableManager = new RemindersTableManager(
                this.clusterOptions.ServiceId,
                this.clusterOptions.ClusterId,
                this.storageOptions,
                this.loggerFactory);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    try
                    {
                        await remTableManager.InitTableAsync();
                        _initializationTask.TrySetResult();
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
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _initializationTask.TrySetCanceled(CancellationToken.None);
            return Task.CompletedTask;
        }

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
                    GrainId = GrainId.Parse(tableEntry.GrainReference),
                    ReminderName = tableEntry.ReminderName,
                    StartAt = LogFormatter.ParseDate(tableEntry.StartAt),
                    Period = TimeSpan.Parse(tableEntry.Period),
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
                if (!tableEntry.ServiceId.Equals(serviceIdStr))
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
                ETag = new ETag(remEntry.ETag),
            };
        }

        public async Task TestOnlyClearTable()
        {
            await _initializationTask.Task;

            await this.remTableManager.DeleteTableEntries();
        }

        public async Task<ReminderTableData> ReadRows(GrainId grainId)
        {
            try
            {
                await _initializationTask.Task;

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

        public async Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            try
            {
                await _initializationTask.Task;

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

        public async Task<ReminderEntry> ReadRow(GrainId grainId, string reminderName)
        {
            try
            {
                await _initializationTask.Task;

                LogDebugReadRow(grainId, reminderName);
                var result = await this.remTableManager.FindReminderEntry(grainId, reminderName);
                return result.Entity is null ? null : ConvertFromTableEntry(result.Entity, result.ETag);
            }
            catch (Exception exc)
            {
                LogWarningReadingReminderRow(exc, grainId, reminderName, this.remTableManager.TableName);
                throw;
            }
        }

        public async Task<string> UpsertRow(ReminderEntry entry)
        {
            try
            {
                await _initializationTask.Task;

                LogDebugUpsertRow(entry);
                ReminderTableEntry remTableEntry = ConvertToTableEntry(entry, this.clusterOptions.ServiceId, this.clusterOptions.ClusterId);

                string result = await this.remTableManager.UpsertRow(remTableEntry);
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
                await _initializationTask.Task;

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
            public override string ToString() => RangeFactory.CreateRange(Begin, End).ToString();
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
