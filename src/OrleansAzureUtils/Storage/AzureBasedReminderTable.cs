using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;


namespace Orleans.Runtime.ReminderService
{
    internal class AzureBasedReminderTable : IReminderTable
    {
        private Logger logger;
        private RemindersTableManager remTableManager;


        public async Task Init(GlobalConfiguration config, Logger logger)
        {
            this.logger = logger;
            remTableManager = await RemindersTableManager.GetManager(config.ServiceId, config.DeploymentId, config.DataConnectionStringForReminders);
        }

        #region Utility methods
        
        private ReminderTableData ConvertFromTableEntryList(IEnumerable<Tuple<ReminderTableEntry, string>> entries)
        {
            var remEntries = new List<ReminderEntry>();
            foreach (var entry in entries)
            {
                try
                {
                    ReminderEntry converted = ConvertFromTableEntry(entry.Item1, entry.Item2);
                    remEntries.Add(converted);
                }
                catch (Exception)
                {
                    // Ignoring...
                }
            }
            return new ReminderTableData(remEntries);
        }

        private ReminderEntry ConvertFromTableEntry(ReminderTableEntry tableEntry, string eTag)
        {
            try
            {
                return new ReminderEntry
                {
                    GrainRef = GrainReference.FromKeyString(tableEntry.GrainReference),
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
                logger.Error(ErrorCode.AzureTable_49, error, exc);
                throw;
            }
            finally
            {
                string serviceIdStr = ReminderTableEntry.ConstructServiceIdStr(remTableManager.ServiceId);
                if (!tableEntry.ServiceId.Equals(serviceIdStr))
                {
                    var error =
                        $"Read a reminder entry for wrong Service id. Read {tableEntry}, but my service id is {serviceIdStr}. Going to discard it.";
                    logger.Warn(ErrorCode.AzureTable_ReadWrongReminder, error);
                    throw new OrleansException(error);
                }
            }
        }

        private static ReminderTableEntry ConvertToTableEntry(ReminderEntry remEntry, Guid serviceId, string deploymentId)
        {
            string partitionKey = ReminderTableEntry.ConstructPartitionKey(serviceId, remEntry.GrainRef);
            string rowKey = ReminderTableEntry.ConstructRowKey(remEntry.GrainRef, remEntry.ReminderName);
            string serviceIdStr = ReminderTableEntry.ConstructServiceIdStr(serviceId);

            var consistentHash = remEntry.GrainRef.GetUniformHashCode();

            return new ReminderTableEntry
            {
                PartitionKey = partitionKey,
                RowKey = rowKey,

                ServiceId = serviceIdStr,
                DeploymentId = deploymentId,
                GrainReference = remEntry.GrainRef.ToKeyString(),
                ReminderName = remEntry.ReminderName,

                StartAt = LogFormatter.PrintDate(remEntry.StartAt),
                Period = remEntry.Period.ToString(),

                GrainRefConsistentHash = String.Format("{0:X8}", consistentHash),
                ETag = remEntry.ETag,
            };
        }

        #endregion 
        
        public Task TestOnlyClearTable()
        {
            return remTableManager.DeleteTableEntries();
        }

        public async Task<ReminderTableData> ReadRows(GrainReference key)
        {
            try
            {
                var entries = await remTableManager.FindReminderEntries(key);
                ReminderTableData data = ConvertFromTableEntryList(entries);
                if (logger.IsVerbose2) logger.Verbose2("Read for grain {0} Table=" + Environment.NewLine + "{1}", key, data.ToString());
                return data;
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.AzureTable_47,
                    $"Intermediate error reading reminders for grain {key} in table {remTableManager.TableName}.", exc);
                throw;
            }
        }

        public async Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            try
            {
                var entries = await remTableManager.FindReminderEntries(begin, end);
                ReminderTableData data = ConvertFromTableEntryList(entries);
                if (logger.IsVerbose2) logger.Verbose2("Read in {0} Table=" + Environment.NewLine + "{1}", RangeFactory.CreateRange(begin, end), data);
                return data;
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.AzureTable_40,
                    $"Intermediate error reading reminders in range {RangeFactory.CreateRange(begin, end)} for table {remTableManager.TableName}.", exc);
                throw;
            }
        }

        public async Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName)
        {
            try
            {
                if (logger.IsVerbose) logger.Verbose("ReadRow grainRef = {0} reminderName = {1}", grainRef, reminderName);
                var result = await remTableManager.FindReminderEntry(grainRef, reminderName);
                return result == null ? null : ConvertFromTableEntry(result.Item1, result.Item2);
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.AzureTable_46,
                    $"Intermediate error reading row with grainId = {grainRef} reminderName = {reminderName} from table {remTableManager.TableName}.", exc);
                throw;
            }
        }

        public async Task<string> UpsertRow(ReminderEntry entry)
        {
            try
            {
                if (logger.IsVerbose) logger.Verbose("UpsertRow entry = {0}", entry.ToString());
                ReminderTableEntry remTableEntry = ConvertToTableEntry(entry, remTableManager.ServiceId, remTableManager.DeploymentId);

                string result = await remTableManager.UpsertRow(remTableEntry);
                if (result == null)
                {
                    logger.Warn(ErrorCode.AzureTable_45,
                        $"Upsert failed on the reminder table. Will retry. Entry = {entry.ToString()}");
                }
                return result;
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.AzureTable_42,
                    $"Intermediate error upserting reminder entry {entry.ToString()} to the table {remTableManager.TableName}.", exc);
                throw;
            }
        }

        public async Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag)
        {
            var entry = new ReminderTableEntry
            {
                PartitionKey = ReminderTableEntry.ConstructPartitionKey(remTableManager.ServiceId, grainRef),
                RowKey = ReminderTableEntry.ConstructRowKey(grainRef, reminderName),
                ETag = eTag,
            };
            try
            {
                if (logger.IsVerbose2) logger.Verbose2("RemoveRow entry = {0}", entry.ToString());

                bool result = await remTableManager.DeleteReminderEntryConditionally(entry, eTag);
                if (result == false)
                {
                    logger.Warn(ErrorCode.AzureTable_43,
                        $"Delete failed on the reminder table. Will retry. Entry = {entry}");
                }
                return result;
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.AzureTable_44,
                    $"Intermediate error when deleting reminder entry {entry} to the table {remTableManager.TableName}.", exc);
                throw;
            }
        }
    }
}
