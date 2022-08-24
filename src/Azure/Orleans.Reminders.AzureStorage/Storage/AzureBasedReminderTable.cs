using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Azure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.AzureUtils.Utilities;
using Orleans.Configuration;
using Orleans.Reminders.AzureStorage;

namespace Orleans.Runtime.ReminderService
{
    public sealed class AzureBasedReminderTable : IReminderTable
    {
        private readonly ILogger logger;
        private readonly ILoggerFactory loggerFactory;
        private readonly ClusterOptions clusterOptions;
        private readonly AzureTableReminderStorageOptions storageOptions;
        private RemindersTableManager remTableManager;

        public AzureBasedReminderTable(
            ILoggerFactory loggerFactory,
            IOptions<ClusterOptions> clusterOptions,
            IOptions<AzureTableReminderStorageOptions> storageOptions)
        {
            this.logger = loggerFactory.CreateLogger<AzureBasedReminderTable>();
            this.loggerFactory = loggerFactory;
            this.clusterOptions = clusterOptions.Value;
            this.storageOptions = storageOptions.Value;
        }

        public async Task Init()
        {
            this.remTableManager = await RemindersTableManager.GetManager(
                this.clusterOptions.ServiceId,
                this.clusterOptions.ClusterId,
                this.loggerFactory,
                options: this.storageOptions);
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
                this.logger.LogError((int)AzureReminderErrorCode.AzureTable_49, exc, "Failed to parse ReminderTableEntry: {TableEntry}. This entry is corrupt, going to ignore it.", tableEntry);
                throw;
            }
            finally
            {
                string serviceIdStr = this.remTableManager.ServiceId;
                if (!tableEntry.ServiceId.Equals(serviceIdStr))
                {
                    this.logger.LogWarning(
                        (int)AzureReminderErrorCode.AzureTable_ReadWrongReminder,
                        "Read a reminder entry for wrong Service id. Read {TableEntry}, but my service id is {ServiceId}. Going to discard it.",
                        tableEntry,
                        serviceIdStr);
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

        public Task TestOnlyClearTable()
        {
            return this.remTableManager.DeleteTableEntries();
        }

        public async Task<ReminderTableData> ReadRows(GrainId grainId)
        {
            try
            {
                var entries = await this.remTableManager.FindReminderEntries(grainId);
                ReminderTableData data = ConvertFromTableEntryList(entries);
                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.LogTrace($"Read for grain {{GrainId}} Table={Environment.NewLine}{{Data}}", grainId, data.ToString());
                return data;
            }
            catch (Exception exc)
            {
                this.logger.LogWarning((int)AzureReminderErrorCode.AzureTable_47,
                    exc,
                    "Intermediate error reading reminders for grain {GrainId} in table {TableName}.", grainId, this.remTableManager.TableName);
                throw;
            }
        }

        public async Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            try
            {
                var entries = await this.remTableManager.FindReminderEntries(begin, end);
                ReminderTableData data = ConvertFromTableEntryList(entries);
                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.LogTrace($"Read in {{RingRange}} Table={Environment.NewLine}{{Data}}", RangeFactory.CreateRange(begin, end), data);
                return data;
            }
            catch (Exception exc)
            {
                this.logger.LogWarning((int)AzureReminderErrorCode.AzureTable_40,
                    exc,
                    "Intermediate error reading reminders in range {RingRange} for table {TableName}.", RangeFactory.CreateRange(begin, end), this.remTableManager.TableName);
                throw;
            }
        }

        public async Task<ReminderEntry> ReadRow(GrainId grainId, string reminderName)
        {
            try
            {
                if (this.logger.IsEnabled(LogLevel.Debug)) this.logger.LogDebug("ReadRow grainRef = {GrainId} reminderName = {ReminderName}", grainId, reminderName);
                var result = await this.remTableManager.FindReminderEntry(grainId, reminderName);
                return result.Entity is null ? null : ConvertFromTableEntry(result.Entity, result.ETag);
            }
            catch (Exception exc)
            {
                this.logger.LogWarning((int)AzureReminderErrorCode.AzureTable_46,
                    exc,
                    "Intermediate error reading row with grainId = {GrainId} reminderName = {ReminderName} from table {TableName}.", grainId, reminderName, this.remTableManager.TableName);
                throw;
            }
        }

        public async Task<string> UpsertRow(ReminderEntry entry)
        {
            try
            {
                if (this.logger.IsEnabled(LogLevel.Debug)) this.logger.LogDebug("UpsertRow entry = {Data}", entry.ToString());
                ReminderTableEntry remTableEntry = ConvertToTableEntry(entry, this.remTableManager.ServiceId, this.remTableManager.ClusterId);

                string result = await this.remTableManager.UpsertRow(remTableEntry);
                if (result == null)
                {
                    this.logger.LogWarning((int)AzureReminderErrorCode.AzureTable_45,
                        "Upsert failed on the reminder table. Will retry. Entry = {Data}", entry.ToString());
                }
                return result;
            }
            catch (Exception exc)
            {
                this.logger.LogWarning((int)AzureReminderErrorCode.AzureTable_42,
                    exc,
                    "Intermediate error upserting reminder entry {Data} to the table {TableName}.", entry.ToString(), this.remTableManager.TableName);
                throw;
            }
        }

        public async Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
        {
            var entry = new ReminderTableEntry
            {
                PartitionKey = ReminderTableEntry.ConstructPartitionKey(this.remTableManager.ServiceId, grainId),
                RowKey = ReminderTableEntry.ConstructRowKey(grainId, reminderName),
                ETag = new ETag(eTag),
            };
            try
            {
                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.LogTrace("RemoveRow entry = {Data}", entry.ToString());

                bool result = await this.remTableManager.DeleteReminderEntryConditionally(entry, eTag);
                if (result == false)
                {
                    this.logger.LogWarning((int)AzureReminderErrorCode.AzureTable_43,
                        "Delete failed on the reminder table. Will retry. Entry = {Data}", entry);
                }
                return result;
            }
            catch (Exception exc)
            {
                this.logger.LogWarning((int)AzureReminderErrorCode.AzureTable_44,
                    exc,
                    "Intermediate error when deleting reminder entry {Data} to the table {TableName}.", entry, this.remTableManager.TableName);
                throw;
            }
        }
    }
}
