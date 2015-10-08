/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;


namespace Orleans.Runtime.ReminderService
{
    internal class AzureBasedReminderTable : IReminderTable
    {
        private TraceLogger logger;
        private RemindersTableManager remTableManager;


        public async Task Init(GlobalConfiguration config, TraceLogger logger)
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
                    StartAt = TraceLogger.ParseDate(tableEntry.StartAt),
                    Period = TimeSpan.Parse(tableEntry.Period),
                    ETag = eTag,
                };
            }
            catch (Exception exc)
            {
                var error = String.Format( "Failed to parse ReminderTableEntry: {0}. This entry is corrupt, going to ignore it.",
                    tableEntry);
                logger.Error(ErrorCode.AzureTable_49, error, exc);
                throw;
            }
            finally
            {
                string serviceIdStr = ReminderTableEntry.ConstructServiceIdStr(remTableManager.ServiceId);
                if (!tableEntry.ServiceId.Equals(serviceIdStr))
                {
                    var error = String.Format( "Read a reminder entry for wrong Service id. Read {0}, but my service id is {1}. Going to discard it.",
                        tableEntry, serviceIdStr);
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

                StartAt = TraceLogger.PrintDate(remEntry.StartAt),
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
                logger.Warn(ErrorCode.AzureTable_47, String.Format("Intermediate error reading reminders for grain {0} in table {1}.",
                    key, remTableManager.TableName), exc);
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
                logger.Warn(ErrorCode.AzureTable_40, String.Format("Intermediate error reading reminders in range {0} for table {1}.",
                     RangeFactory.CreateRange(begin, end), remTableManager.TableName), exc);
                throw;
            }
        }

        public async Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName)
        {
            try
            {
                if (logger.IsVerbose) logger.Verbose("ReadRow grainRef = {0} reminderName = {1}", grainRef, reminderName);
                var result = await remTableManager.FindReminderEntry(grainRef, reminderName);
                return ConvertFromTableEntry(result.Item1, result.Item2);
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.AzureTable_46, String.Format("Intermediate error reading row with grainId = {0} reminderName = {1} from table {2}.",
                    grainRef, reminderName, remTableManager.TableName), exc);
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
                    logger.Warn(ErrorCode.AzureTable_45, String.Format("Upsert failed on the reminder table. Will retry. Entry = {0}", entry.ToString()));
                }
                return result;
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.AzureTable_42, String.Format("Intermediate error upserting reminder entry {0} to the table {1}.",
                    entry.ToString(), remTableManager.TableName), exc);
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
                    logger.Warn(ErrorCode.AzureTable_43, String.Format("Delete failed on the reminder table. Will retry. Entry = {0}", entry));
                }
                return result;
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.AzureTable_44, String.Format("Intermediate error when deleting reminder entry {0} to the table {1}.",
                    entry, remTableManager.TableName), exc);
                throw;
            }
        }
    }
}
