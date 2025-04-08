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
    internal sealed partial class ReminderTableGrain : Grain, IReminderTableGrain, IGrainMigrationParticipant
    {
        private readonly ILogger _logger;
        private Dictionary<GrainId, Dictionary<string, ReminderEntry>> _reminderTable = new();

        public ReminderTableGrain(ILogger<ReminderTableGrain> logger)
        {
            _logger = logger;
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            LogDebugActivated();
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            LogDebugDeactivated();
            return Task.CompletedTask;
        }

        public Task TestOnlyClearTable()
        {
            LogDebugTestOnlyClearTable();
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

            LogTraceSelectedReminders(list.Count, new(_reminderTable), range, new(list));

            var result = new ReminderTableData(list);
            LogDebugReadReminders(result.Reminders.Count, new(result.Reminders));
            return Task.FromResult(result);
        }

        public Task<ReminderEntry> ReadRow(GrainId grainId, string reminderName)
        {
            ReminderEntry result = null;
            if (_reminderTable.TryGetValue(grainId, out var reminders))
            {
                reminders.TryGetValue(reminderName, out result);
            }

            if (result is null)
            {
                LogTraceReminderNotFound(grainId, reminderName);
            }
            else
            {
                LogTraceReadRow(grainId, reminderName, result);
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
            LogTraceUpsertedEntry(entry, old);

            return Task.FromResult(entry.ETag);
        }

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
        {
            LogDebugRemoveRow(grainId, reminderName, eTag);
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

            LogWarningRemoveRow(grainId, reminderName, eTag, new(_reminderTable));
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

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Activated"
        )]
        private partial void LogDebugActivated();

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Deactivated"
        )]
        private partial void LogDebugDeactivated();

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "TestOnlyClearTable"
        )]
        private partial void LogDebugTestOnlyClearTable();

        private readonly struct TotalCountLogRecord(Dictionary<GrainId, Dictionary<string, ReminderEntry>> reminderTable)
        {
            public override string ToString() => reminderTable.Values.Sum(r => r.Count).ToString();
        }

        private readonly struct RemindersLogRecord(IEnumerable<ReminderEntry> reminders)
        {
            public override string ToString() => Utils.EnumerableToString(reminders);
        }

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Selected {SelectCount} out of {TotalCount} reminders from memory for {Range}. Selected: {Reminders}"
        )]
        private partial void LogTraceSelectedReminders(int selectCount, TotalCountLogRecord totalCount, IRingRange range, RemindersLogRecord reminders);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Read {ReminderCount} reminders from memory: {Reminders}"
        )]
        private partial void LogDebugReadReminders(int reminderCount, RemindersLogRecord reminders);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Reminder not found for grain {Grain} reminder {ReminderName}"
        )]
        private partial void LogTraceReminderNotFound(GrainId grain, string reminderName);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Read for grain {Grain} reminder {ReminderName} row {Reminder}"
        )]
        private partial void LogTraceReadRow(GrainId grain, string reminderName, ReminderEntry reminder);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Upserted entry {Updated}, replaced {Replaced}"
        )]
        private partial void LogTraceUpsertedEntry(ReminderEntry updated, ReminderEntry replaced);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "RemoveRow Grain = {Grain}, ReminderName = {ReminderName}, eTag = {ETag}"
        )]
        private partial void LogDebugRemoveRow(GrainId grain, string reminderName, string eTag);

        private readonly struct NewValuesLogRecord(Dictionary<GrainId, Dictionary<string, ReminderEntry>> reminderTable)
        {
            public override string ToString() => Utils.EnumerableToString(reminderTable.Values.SelectMany(x => x.Values));
        }

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)RSErrorCode.RS_Table_Remove,
            Message = "RemoveRow failed for Grain = {Grain}, ReminderName = {ReminderName}, eTag = {ETag}. Table now is: {NewValues}"
        )]
        private partial void LogWarningRemoveRow(GrainId grain, string reminderName, string eTag, NewValuesLogRecord newValues);
    }
}
