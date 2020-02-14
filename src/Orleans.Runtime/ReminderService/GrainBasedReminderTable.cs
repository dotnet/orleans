using System;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.MultiCluster;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Runtime.ReminderService
{
    [Reentrant]
    internal class GrainBasedReminderTable : Grain, IReminderTableGrain
    {
        private Table remTable;
        private ILogger logger;

        public override Task OnActivateAsync()
        {
            var loggerFactory = this.ServiceProvider.GetRequiredService<ILoggerFactory>();
            logger = loggerFactory.CreateLogger(String.Format("{0}_{1}", typeof(GrainBasedReminderTable).FullName, Data.Address.ToString()));
            logger.Info("GrainBasedReminderTable {0} Activated. Full identity: {1}", Identity, Data.Address.ToFullString());
            remTable = new Table(loggerFactory);
            base.DelayDeactivation(TimeSpan.FromDays(10 * 365)); // Delay Deactivation for GrainBasedReminderTable virtually indefinitely.
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("GrainBasedReminderTable {0} OnDeactivateAsync. Full identity: {1}", Identity, Data.Address.ToFullString());
            return Task.CompletedTask;
        }

        public Task<ReminderTableData> ReadRows(GrainReference grainRef)
        {
            return Task.FromResult(remTable.ReadRows(grainRef));
        }

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            ReminderTableData t = remTable.ReadRows(begin, end);
            logger.Debug("Read {0} reminders from memory: {1}, {2}", t.Reminders.Count, Environment.NewLine, Utils.EnumerableToString(t.Reminders));
            return Task.FromResult(t);
        }

        public Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName)
        {
            return Task.FromResult(remTable.ReadRow(grainRef, reminderName));
        }

        public Task<string> UpsertRow(ReminderEntry entry)
        {
            return Task.FromResult(remTable.UpsertRow(entry));
        }

        /// <summary>
        /// Remove a row from the table
        /// </summary>
        /// <param name="grainRef"></param>
        /// <param name="reminderName"></param>
        /// <param name="eTag"></param>
        /// <returns>true if a row with <paramref name="grainRef"/> and <paramref name="reminderName"/> existed and was removed successfully, false otherwise</returns>
        public Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("RemoveRow entry grainRef = {0}, reminderName = {1}, eTag = {2}", grainRef, reminderName, eTag);
            bool result = remTable.RemoveRow(grainRef, reminderName, eTag);
            if (result == false)
            {
                logger.Warn(ErrorCode.RS_Table_Remove, "RemoveRow failed for grainRef = {0}, ReminderName = {1}, eTag = {2}. Table now is: {3}",
                    grainRef.ToDetailedString(), reminderName, eTag, remTable.ReadAll());
            }
            return Task.FromResult(result);
        }

        public Task TestOnlyClearTable()
        {
            logger.Info("TestOnlyClearTable");
            remTable.Reset();
            return Task.CompletedTask;
        }

        [Serializable]
        private class Table
        {
            // key: GrainReference
            // value: V
            //      V.key: ReminderName
            //      V.Value: ReminderEntry
            private Dictionary<GrainReference, Dictionary<string, ReminderEntry>> reminderTable;

            [NonSerialized]
            private readonly ILogger logger;

            public Table(ILoggerFactory loggerFactory)
            {
                this.logger = loggerFactory.CreateLogger<ILoggerFactory>();
                Reset();
            }

            public ReminderTableData ReadRows(GrainReference grainRef)
            {
                Dictionary<string, ReminderEntry> reminders;
                reminderTable.TryGetValue(grainRef, out reminders);
                return reminders == null ? new ReminderTableData() :
                    new ReminderTableData(reminders.Values.ToList());
            }

            public ReminderTableData ReadRows(uint begin, uint end)
            {
                var range = RangeFactory.CreateRange(begin, end);
                IEnumerable<GrainReference> keys = reminderTable.Keys.Where(range.InRange);

                // is there a sleaker way of doing this in C#?
                var list = new List<ReminderEntry>();
                foreach (GrainReference k in keys)
                    list.AddRange(reminderTable[k].Values);

                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Selected {0} out of {1} reminders from memory for {2}. List is: {3}{4}", list.Count, reminderTable.Count, range.ToString(),
                    Environment.NewLine, Utils.EnumerableToString(list, e => e.ToString()));

                return new ReminderTableData(list);
            }

            public ReminderEntry ReadRow(GrainReference grainRef, string reminderName)
            {
                ReminderEntry result = null;
                Dictionary<string, ReminderEntry> reminders;
                if (reminderTable.TryGetValue(grainRef, out reminders))
                {
                    reminders.TryGetValue(reminderName, out result);
                }

                if (logger.IsEnabled(LogLevel.Trace))
                {
                    if (result == null)
                        logger.Trace("Reminder not found for grain {0} reminder {1} ", grainRef, reminderName);
                    else
                        logger.Trace("Read for grain {0} reminder {1} row {2}", grainRef, reminderName, result.ToString());
                }
                return result;
            }

            public string UpsertRow(ReminderEntry entry)
            {
                entry.ETag = Guid.NewGuid().ToString();
                Dictionary<string, ReminderEntry> d;
                if (!reminderTable.ContainsKey(entry.GrainRef))
                {
                    d = new Dictionary<string, ReminderEntry>();
                    reminderTable.Add(entry.GrainRef, d);
                }
                d = reminderTable[entry.GrainRef];

                ReminderEntry old; // tracing purposes only
                d.TryGetValue(entry.ReminderName, out old); // tracing purposes only
                                                            // add or over-write
                d[entry.ReminderName] = entry;
                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Upserted entry {0}, replaced {1}", entry, old);
                return entry.ETag;
            }

            public bool RemoveRow(GrainReference grainRef, string reminderName, string eTag)
            {
                Dictionary<string, ReminderEntry> data = null;
                ReminderEntry e = null;

                // assuming the calling grain executes one call at a time, so no need to lock
                if (!reminderTable.TryGetValue(grainRef, out data))
                {
                    return false;
                }

                data.TryGetValue(reminderName, out e); // check if eTag matches
                if (e == null || !e.ETag.Equals(eTag))
                {
                    return false;
                }

                if (!data.Remove(reminderName))
                {
                    return false;
                }

                if (data.Count == 0)
                {
                    reminderTable.Remove(grainRef);
                }
                return true;
            }

            // use only for internal printing during testing ... the reminder table can be huge in a real deployment!
            public ReminderTableData ReadAll()
            {
                // is there a sleaker way of doing this in C#?
                var list = new List<ReminderEntry>();
                foreach (GrainReference k in reminderTable.Keys)
                {
                    list.AddRange(reminderTable[k].Values);
                }
                return new ReminderTableData(list);
            }

            public void Reset()
            {
                reminderTable = new Dictionary<GrainReference, Dictionary<string, ReminderEntry>>();
            }
        }
    }
}
