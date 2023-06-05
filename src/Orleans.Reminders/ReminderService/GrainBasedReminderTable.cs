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
    internal sealed class ReminderTableGrain : Grain, IReminderTableGrain, IGrainMigrationParticipant
    {
        private readonly ILogger _logger;
        private Dictionary<GrainId, Dictionary<string, ReminderEntry>> _reminderTable = new();

        public ReminderTableGrain(ILogger<ReminderTableGrain> logger)
        {
            _logger = logger;
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Activated");
            }

            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Deactivated");
            }

            return Task.CompletedTask;
        }

        public Task TestOnlyClearTable()
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("TestOnlyClearTable");
            }

            _reminderTable.Clear();
            return Task.CompletedTask;
        }

        public Task<ReminderTableData> ReadRows(GrainId grainId)
        {
            var result = _reminderTable.TryGetValue(grainId, out var reminders) ? new ReminderTableData(reminders.Values) : new();
            return Task.FromResult(result);
        }

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            var range = RangeFactory.CreateRange(begin, end);

            var list = new List<ReminderEntry>();
            foreach (var e in _reminderTable)
                if (range.InRange(e.Key))
                    list.AddRange(e.Value.Values);

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace(
                    "Selected {SelectCount} out of {TotalCount} reminders from memory for {Range}. Selected: {Reminders}",
                    list.Count,
                    _reminderTable.Values.Sum(r => r.Count),
                    range.ToString(),
                    Utils.EnumerableToString(list));
            }

            var result = new ReminderTableData(list);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Read {ReminderCount} reminders from memory: {Reminders}", result.Reminders.Count, Utils.EnumerableToString(result.Reminders));
            }

            return Task.FromResult(result);
        }

        public Task<ReminderEntry> ReadRow(GrainId grainId, string reminderName)
        {
            ReminderEntry result = null;
            if (_reminderTable.TryGetValue(grainId, out var reminders))
            {
                reminders.TryGetValue(reminderName, out result);
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                if (result is null)
                {
                    _logger.LogTrace("Reminder not found for grain {Grain} reminder {ReminderName} ", grainId, reminderName);
                }
                else
                {
                    _logger.LogTrace("Read for grain {Grain} reminder {ReminderName} row {Reminder}", grainId, reminderName, result.ToString());
                }
            }

            return Task.FromResult(result);
        }

        public Task<string> UpsertRow(ReminderEntry entry)
        {
            entry.ETag = Guid.NewGuid().ToString();
            var d = CollectionsMarshal.GetValueRefOrAddDefault(_reminderTable, entry.GrainId, out _) ??= new();
            ref var entryRef = ref CollectionsMarshal.GetValueRefOrAddDefault(d, entry.ReminderName, out _);

            var old = entryRef; // tracing purposes only
            entryRef = entry;
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Upserted entry {Updated}, replaced {Replaced}", entry, old);
            }

            return Task.FromResult(entry.ETag);
        }

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("RemoveRow Grain = {Grain}, ReminderName = {ReminderName}, eTag = {ETag}", grainId, reminderName, eTag);
            }

            if (_reminderTable.TryGetValue(grainId, out var data)
                && data.TryGetValue(reminderName, out var e)
                && e.ETag == eTag)
            {
                if (data.Count > 1)
                {
                    data.Remove(reminderName);
                }
                else
                {
                    _reminderTable.Remove(grainId);
                }

                return Task.FromResult(true);
            }

            _logger.LogWarning(
                (int)RSErrorCode.RS_Table_Remove,
                "RemoveRow failed for Grain = {Grain}, ReminderName = {ReminderName}, eTag = {ETag}. Table now is: {NewValues}",
                grainId,
                reminderName,
                eTag,
                Utils.EnumerableToString(_reminderTable.Values.SelectMany(x => x.Values)));

            return Task.FromResult(false);
        }

        void IGrainMigrationParticipant.OnDehydrate(IDehydrationContext dehydrationContext)
        {
            dehydrationContext.TryAddValue("table", _reminderTable);
        }

        void IGrainMigrationParticipant.OnRehydrate(IRehydrationContext rehydrationContext)
        {
            if (rehydrationContext.TryGetValue("table", out Dictionary<GrainId, Dictionary<string, ReminderEntry>> table))
            {
                _reminderTable = table;
            }
        }
    }
}
