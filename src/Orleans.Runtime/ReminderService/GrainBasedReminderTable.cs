using System;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.MultiCluster;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Runtime.ReminderService
{
    [Reentrant]
    [OneInstancePerCluster]
    internal class GrainBasedReminderTable : Grain, IReminderTableGrain
    {
        private readonly Table remTable;
        private readonly ILogger logger;

        public GrainBasedReminderTable(ILogger<GrainBasedReminderTable> logger)
        {
            this.logger = logger;
            remTable = new Table(logger);
        }

        public override Task OnActivateAsync()
        {
            logger.LogInformation("Activated");
            base.DelayDeactivation(TimeSpan.FromDays(10 * 365)); // Delay Deactivation for GrainBasedReminderTable virtually indefinitely.
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync()
        {
            logger.LogInformation("Deactivated");
            return Task.CompletedTask;
        }

        public Task<ReminderTableData> ReadRows(GrainReference grainRef)
        {
            return Task.FromResult(remTable.ReadRows(grainRef));
        }

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            ReminderTableData t = remTable.ReadRows(begin, end);
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Read {ReminderCount} reminders from memory: {Reminders}", t.Reminders.Count, Utils.EnumerableToString(t.Reminders));
            }

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
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("RemoveRow Grain = {Grain}, ReminderName = {ReminderName}, eTag = {ETag}", grainRef, reminderName, eTag);
            }

            var result = remTable.RemoveRow(grainRef, reminderName, eTag);
            if (!result)
            {
                logger.LogWarning(
                    (int)ErrorCode.RS_Table_Remove,
                    "RemoveRow Grain = {Grain}, ReminderName = {ReminderName}, eTag = {ETag}. Table now is: {3}",
                    grainRef,
                    reminderName,
                    eTag,
                    Utils.EnumerableToString(remTable.ReadAll().Reminders));
            }

            return Task.FromResult(result);
        }

        public Task TestOnlyClearTable()
        {
            logger.LogInformation("TestOnlyClearTable");
            remTable.Reset();
            return Task.CompletedTask;
        }

        private class Table
        {
            // key: GrainReference
            // value: V
            //      V.key: ReminderName
            //      V.Value: ReminderEntry
            private readonly Dictionary<GrainReference, Dictionary<string, ReminderEntry>> reminderTable = new Dictionary<GrainReference, Dictionary<string, ReminderEntry>>();
            private readonly ILogger logger;

            public Table(ILogger logger)
            {
                this.logger = logger;
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
                {
                    list.AddRange(reminderTable[k].Values);
                }

                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace(
                        "Selected {SelectCount} out of {TotalCount} reminders from memory for {Range}. Selected: {Reminders}",
                        list.Count,
                        reminderTable.Count,
                        range.ToString(),
                        Utils.EnumerableToString(list, e => e.ToString()));
                }

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
                    if (result is null)
                    {
                        logger.LogTrace("Reminder not found for grain {Grain} reminder {ReminderName} ", grainRef, reminderName);
                    }
                    else
                    {
                        logger.LogTrace("Read for grain {Grain} reminder {ReminderName} row {Reminder}", grainRef, reminderName, result.ToString());
                    }
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
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace("Upserted entry {Updated}, replaced {Replaced}", entry, old);
                }

                return entry.ETag;
            }

            public bool RemoveRow(GrainReference grainRef, string reminderName, string eTag)
            {
                // assuming the calling grain executes one call at a time, so no need to lock
                if (!reminderTable.TryGetValue(grainRef, out var data))
                {
                    return false;
                }

                data.TryGetValue(reminderName, out var e); // check if eTag matches
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
                reminderTable.Clear();
            }
        }
    }
}
