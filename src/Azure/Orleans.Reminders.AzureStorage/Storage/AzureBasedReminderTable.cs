using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.AzureUtils.Utilities;
using Orleans.Configuration;
using Orleans.Reminders.AzureStorage;
using Orleans.Reminders.AzureStorage.Storage.Reminders;

namespace Orleans.Runtime.ReminderService
{
    public class AzureBasedReminderTable : IReminderTable
    {
        private readonly IGrainReferenceConverter grainReferenceConverter;
        private readonly ILogger logger;
        private readonly ILoggerFactory loggerFactory;
        private readonly ClusterOptions clusterOptions;
        private readonly AzureTableReminderStorageOptions storageOptions;
        private RemindersTableManager remTableManager;
        private readonly IReminderTableEntryBuilder reminderTableEntryBuilder;

        public AzureBasedReminderTable(
            IGrainReferenceConverter grainReferenceConverter,
            ILoggerFactory loggerFactory,
            IOptions<ClusterOptions> clusterOptions,
            IOptions<AzureTableReminderStorageOptions> storageOptions,
            IReminderTableEntryBuilder reminderTableEntryBuilder)
            : this(grainReferenceConverter, loggerFactory, clusterOptions.Value, storageOptions.Value, reminderTableEntryBuilder)
        {
        }

        public AzureBasedReminderTable(
            IGrainReferenceConverter grainReferenceConverter,
            ILoggerFactory loggerFactory,
            ClusterOptions clusterOptions,
            AzureTableReminderStorageOptions storageOptions,
            IReminderTableEntryBuilder reminderTableEntryBuilder)
        {
            this.grainReferenceConverter = grainReferenceConverter;
            this.reminderTableEntryBuilder = reminderTableEntryBuilder;

            this.logger = loggerFactory.CreateLogger<AzureBasedReminderTable>();
            this.loggerFactory = loggerFactory;
            this.clusterOptions = clusterOptions;
            this.storageOptions = storageOptions;
            this.remTableManager = new RemindersTableManager(
                this.clusterOptions.ServiceId,
                this.clusterOptions.ClusterId,
                this.storageOptions,
                this.loggerFactory);
        }

        public async Task Init()
        {
            try
            {
                logger.Info("Creating RemindersTableManager for service id {0} and cluster id {1}.", clusterOptions.ServiceId, clusterOptions.ClusterId);
                await remTableManager.InitTableAsync();
            }
            catch (Exception ex)
            {
                string errorMsg = $"Exception trying to create or connect to the Azure table: {ex.Message}";
                logger.Error((int)AzureReminderErrorCode.AzureTable_39, errorMsg, ex);
                throw new OrleansException(errorMsg, ex);
            }
        }

        private ReminderTableData ConvertFromTableEntryList(List<(ReminderTableEntry Entity, string ETag)> entries)
        {
            var remEntries = new List<ReminderEntry>();
            foreach (var entry in entries)
            {
                try
                {
                    ReminderEntry converted = ConvertFromTableEntry(entry.Entity, entry.ETag);
                    remEntries.Add(converted);
                }
                catch (Exception)
                {
                    // Ignoring...
                }
            }
            return new ReminderTableData(remEntries);
        }

        public Task TestOnlyClearTable()
        {
            return this.remTableManager.DeleteTableEntries();
        }

        public async Task<ReminderTableData> ReadRows(GrainReference key)
        {
            try
            {
                var entries = await this.remTableManager.FindReminderEntries(key);
                ReminderTableData data = ConvertFromTableEntryList(entries);
                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace("Read for grain {0} Table=" + Environment.NewLine + "{1}", key, data.ToString());
                return data;
            }
            catch (Exception exc)
            {
                this.logger.Warn((int)AzureReminderErrorCode.AzureTable_47,
                    $"Intermediate error reading reminders for grain {key} in table {this.remTableManager.TableName}.", exc);
                throw;
            }
        }

        public async Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            try
            {
                var entries = await this.remTableManager.FindReminderEntries(begin, end);
                ReminderTableData data = ConvertFromTableEntryList(entries);
                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace("Read in {0} Table=" + Environment.NewLine + "{1}", RangeFactory.CreateRange(begin, end), data);
                return data;
            }
            catch (Exception exc)
            {
                this.logger.Warn((int)AzureReminderErrorCode.AzureTable_40,
                    $"Intermediate error reading reminders in range {RangeFactory.CreateRange(begin, end)} for table {this.remTableManager.TableName}.", exc);
                throw;
            }
        }

        public async Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName)
        {
            try
            {
                if (this.logger.IsEnabled(LogLevel.Debug)) this.logger.Debug("ReadRow grainRef = {0} reminderName = {1}", grainRef, reminderName);
                var partitionKey = reminderTableEntryBuilder.ConstructPartitionKey(this.remTableManager.ServiceId, grainRef);
                var rowKey = reminderTableEntryBuilder.ConstructRowKey(grainRef, reminderName);

                var result = await this.remTableManager.ReadSingleTableEntryAsync(partitionKey, rowKey);
                return result.Entity is null ? null : ConvertFromTableEntry(result.Entity, result.ETag);
            }
            catch (Exception exc)
            {
                this.logger.Warn((int)AzureReminderErrorCode.AzureTable_46,
                    $"Intermediate error reading row with grainId = {grainRef} reminderName = {reminderName} from table {this.remTableManager.TableName}.", exc);
                throw;
            }
        }

        public async Task<string> UpsertRow(ReminderEntry entry)
        {
            try
            {
                if (this.logger.IsEnabled(LogLevel.Debug)) this.logger.Debug("UpsertRow entry = {0}", entry.ToString());
                ReminderTableEntry remTableEntry = ConvertToTableEntry(entry, this.remTableManager.ServiceId, this.remTableManager.ClusterId);

                string result = await this.remTableManager.UpsertRow(remTableEntry);
                if (result == null)
                {
                    this.logger.Warn((int)AzureReminderErrorCode.AzureTable_45,
                        $"Upsert failed on the reminder table. Will retry. Entry = {entry.ToString()}");
                }
                return result;
            }
            catch (Exception exc)
            {
                this.logger.Warn((int)AzureReminderErrorCode.AzureTable_42,
                    $"Intermediate error upserting reminder entry {entry.ToString()} to the table {this.remTableManager.TableName}.", exc);
                throw;
            }
        }

        public async Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag)
        {
            var entry = new ReminderTableEntry
            {
                PartitionKey = reminderTableEntryBuilder.ConstructPartitionKey(this.remTableManager.ServiceId, grainRef),
                RowKey = reminderTableEntryBuilder.ConstructRowKey(grainRef, reminderName),
                ETag = new ETag(eTag),
            };
            try
            {
                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace("RemoveRow entry = {0}", entry.ToString());

                bool result = await this.remTableManager.DeleteReminderEntryConditionally(entry, eTag);
                if (result == false)
                {
                    this.logger.Warn((int)AzureReminderErrorCode.AzureTable_43,
                        $"Delete failed on the reminder table. Will retry. Entry = {entry}");
                }
                return result;
            }
            catch (Exception exc)
            {
                this.logger.Warn((int)AzureReminderErrorCode.AzureTable_44,
                    $"Intermediate error when deleting reminder entry {entry} to the table {this.remTableManager.TableName}.", exc);
                throw;
            }
        }

        private ReminderEntry ConvertFromTableEntry(ReminderTableEntry tableEntry, string eTag)
        {
            try
            {
                var grainRef = reminderTableEntryBuilder.GetGrainReference(tableEntry.GrainReference);

                return new ReminderEntry
                {
                    GrainRef = grainRef,
                    ReminderName = tableEntry.ReminderName,
                    StartAt = LogFormatter.ParseDate(tableEntry.StartAt),
                    Period = TimeSpan.Parse(tableEntry.Period),
                    ETag = eTag,
                };
            }
            catch (Exception exc)
            {
                var error =
                    $"Failed to parse ReminderTableEntry: {tableEntry}. This entry is corrupt, going to ignore it.";
                this.logger.Error((int)AzureReminderErrorCode.AzureTable_49, error, exc);
                throw;
            }
            finally
            {
                string serviceIdStr = this.remTableManager.ServiceId;
                if (!tableEntry.ServiceId.Equals(serviceIdStr))
                {
                    var error =
                        $"Read a reminder entry for wrong Service id. Read {tableEntry}, but my service id is {serviceIdStr}. Going to discard it.";
                    this.logger.Warn((int)AzureReminderErrorCode.AzureTable_ReadWrongReminder, error);
                    throw new OrleansException(error);
                }
            }
        }

        private ReminderTableEntry ConvertToTableEntry(ReminderEntry remEntry, string serviceId, string deploymentId)
        {
            string partitionKey = reminderTableEntryBuilder.ConstructPartitionKey(serviceId, remEntry.GrainRef);
            string rowKey = reminderTableEntryBuilder.ConstructRowKey(remEntry.GrainRef, remEntry.ReminderName);
            var consistentHash = remEntry.GrainRef.GetUniformHashCode();

            return new ReminderTableEntry
            {
                PartitionKey = partitionKey,
                RowKey = rowKey,

                ServiceId = serviceId,
                DeploymentId = deploymentId,
                GrainReference = reminderTableEntryBuilder.GetGrainReference(remEntry.GrainRef),
                ReminderName = remEntry.ReminderName,

                StartAt = LogFormatter.PrintDate(remEntry.StartAt),
                Period = remEntry.Period.ToString(),

                GrainRefConsistentHash = string.Format("{0:X8}", consistentHash),
                ETag = new ETag(remEntry.ETag),
            };
        }

        internal static IReminderTable Create(IServiceProvider serviceProvider, string name)
        {
            var grainRefConverter = serviceProvider.GetService<IGrainReferenceConverter>();
            var grainRefRuntime = serviceProvider.GetService<IGrainReferenceRuntime>();

            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var clusterOptions = serviceProvider.GetRequiredService<IOptions<ClusterOptions>>();
            var storageOptions = serviceProvider.GetRequiredService<IOptionsMonitor<AzureTableReminderStorageOptions>>().Get(name);
            var reminderTableEntryBuilder = new DefaultReminderTableEntryBuilder(grainRefRuntime);

            return new AzureBasedReminderTable(grainRefConverter, loggerFactory, clusterOptions.Value, storageOptions, reminderTableEntryBuilder);
        }
    }
}
