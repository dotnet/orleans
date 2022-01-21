using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;

namespace Orleans.Runtime.ReminderService
{
    [Reentrant]
    [KeepAlive]
    internal class ReminderTableGrain : Grain, IReminderTableGrain
    {
        private readonly Dictionary<GrainReference, Dictionary<string, ReminderEntry>> reminderTable = new Dictionary<GrainReference, Dictionary<string, ReminderEntry>>();
        private readonly ILogger logger;

        public ReminderTableGrain(ILogger<ReminderTableGrain> logger)
        {
            this.logger = logger;
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("Activated");
            base.DelayDeactivation(TimeSpan.FromDays(10 * 365)); // Delay Deactivation virtually indefinitely.
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            logger.LogInformation("Deactivated");
            return Task.CompletedTask;
        }

        public Task TestOnlyClearTable()
        {
            logger.LogInformation("TestOnlyClearTable");
            reminderTable.Clear();
            return Task.CompletedTask;
        }

        public Task<ReminderTableData> ReadRows(GrainReference grainRef)
        {
            Dictionary<string, ReminderEntry> reminders;
            reminderTable.TryGetValue(grainRef, out reminders);
            var result = reminders == null ? new ReminderTableData() : new ReminderTableData(reminders.Values.ToList());
            return Task.FromResult(result);
        }

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            var range = RangeFactory.CreateRange(begin, end);

            var list = reminderTable.Where(e => range.InRange(e.Key)).SelectMany(e => e.Value.Values).ToList();

            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace(
                    "Selected {SelectCount} out of {TotalCount} reminders from memory for {Range}. Selected: {Reminders}",
                    list.Count,
                    reminderTable.Values.Sum(r => r.Count),
                    range.ToString(),
                    Utils.EnumerableToString(list, e => e.ToString()));
            }

            var result = new ReminderTableData(list);
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Read {ReminderCount} reminders from memory: {Reminders}", result.Reminders.Count, Utils.EnumerableToString(result.Reminders));
            }

            return Task.FromResult(result);
        }

        public Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName)
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

            return Task.FromResult(result);
        }

        public Task<string> UpsertRow(ReminderEntry entry)
        {
            entry.ETag = Guid.NewGuid().ToString();
            Dictionary<string, ReminderEntry> d;
            if (!reminderTable.TryGetValue(entry.GrainRef, out d))
            {
                d = new Dictionary<string, ReminderEntry>();
                reminderTable.Add(entry.GrainRef, d);
            }

            ReminderEntry old; // tracing purposes only
            d.TryGetValue(entry.ReminderName, out old); // tracing purposes only
                                                        // add or over-write
            d[entry.ReminderName] = entry;
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace("Upserted entry {Updated}, replaced {Replaced}", entry, old);
            }

            return Task.FromResult(entry.ETag);
        }

        public Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("RemoveRow Grain = {Grain}, ReminderName = {ReminderName}, eTag = {ETag}", grainRef, reminderName, eTag);
            }

            if (reminderTable.TryGetValue(grainRef, out var data)
                && data.TryGetValue(reminderName, out var e)
                && e.ETag == eTag)
            {
                if (data.Count > 1)
                {
                    data.Remove(reminderName);
                }
                else
                {
                    reminderTable.Remove(grainRef);
                }

                return Task.FromResult(true);
            }

            logger.LogWarning(
                (int)ErrorCode.RS_Table_Remove,
                "RemoveRow failed for Grain = {Grain}, ReminderName = {ReminderName}, eTag = {ETag}. Table now is: {3}",
                grainRef,
                reminderName,
                eTag,
                Utils.EnumerableToString(reminderTable.Values.SelectMany(x => x.Values)));

            return Task.FromResult(false);
        }
    }
}
