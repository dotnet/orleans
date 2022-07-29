using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Reminders;

namespace Orleans.Runtime.ReminderService
{
    [Reentrant]
    [KeepAlive]
    internal sealed class ReminderTableGrain : Grain, IReminderTableGrain
    {
        private readonly Dictionary<GrainId, Dictionary<string, ReminderEntry>> reminderTable = new();
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

        public Task<ReminderTableData> ReadRows(GrainId grainId)
        {
            var result = reminderTable.TryGetValue(grainId, out var reminders) ? new ReminderTableData(reminders.Values) : new();
            return Task.FromResult(result);
        }

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            var range = RangeFactory.CreateRange(begin, end);

            var list = new List<ReminderEntry>();
            foreach (var e in reminderTable)
                if (range.InRange(e.Key))
                    list.AddRange(e.Value.Values);

            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace(
                    "Selected {SelectCount} out of {TotalCount} reminders from memory for {Range}. Selected: {Reminders}",
                    list.Count,
                    reminderTable.Values.Sum(r => r.Count),
                    range.ToString(),
                    Utils.EnumerableToString(list));
            }

            var result = new ReminderTableData(list);
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Read {ReminderCount} reminders from memory: {Reminders}", result.Reminders.Count, Utils.EnumerableToString(result.Reminders));
            }

            return Task.FromResult(result);
        }

        public Task<ReminderEntry> ReadRow(GrainId grainId, string reminderName)
        {
            ReminderEntry result = null;
            if (reminderTable.TryGetValue(grainId, out var reminders))
            {
                reminders.TryGetValue(reminderName, out result);
            }

            if (logger.IsEnabled(LogLevel.Trace))
            {
                if (result is null)
                {
                    logger.LogTrace("Reminder not found for grain {Grain} reminder {ReminderName} ", grainId, reminderName);
                }
                else
                {
                    logger.LogTrace("Read for grain {Grain} reminder {ReminderName} row {Reminder}", grainId, reminderName, result.ToString());
                }
            }

            return Task.FromResult(result);
        }

        public Task<string> UpsertRow(ReminderEntry entry)
        {
            entry.ETag = Guid.NewGuid().ToString();
            var d = CollectionsMarshal.GetValueRefOrAddDefault(reminderTable, entry.GrainId, out _) ??= new();
            ref var entryRef = ref CollectionsMarshal.GetValueRefOrAddDefault(d, entry.ReminderName, out _);

            var old = entryRef; // tracing purposes only
            entryRef = entry;
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace("Upserted entry {Updated}, replaced {Replaced}", entry, old);
            }

            return Task.FromResult(entry.ETag);
        }

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("RemoveRow Grain = {Grain}, ReminderName = {ReminderName}, eTag = {ETag}", grainId, reminderName, eTag);
            }

            if (reminderTable.TryGetValue(grainId, out var data)
                && data.TryGetValue(reminderName, out var e)
                && e.ETag == eTag)
            {
                if (data.Count > 1)
                {
                    data.Remove(reminderName);
                }
                else
                {
                    reminderTable.Remove(grainId);
                }

                return Task.FromResult(true);
            }

            logger.LogWarning(
                (int)RSErrorCode.RS_Table_Remove,
                "RemoveRow failed for Grain = {Grain}, ReminderName = {ReminderName}, eTag = {ETag}. Table now is: {NewValues}",
                grainId,
                reminderName,
                eTag,
                Utils.EnumerableToString(reminderTable.Values.SelectMany(x => x.Values)));

            return Task.FromResult(false);
        }
    }
}
