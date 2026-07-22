using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Placement;

namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal interface IAdvancedReminderRecoveryGrain : IGrainWithIntegerKey
{
    Task StartAsync(bool force, CancellationToken cancellationToken);
}

[KeepAlive]
[PreferLocalPlacement]
internal sealed class AdvancedReminderRecoveryGrain(
    IReminderTable reminderTable,
    IGrainFactory grainFactory) : Grain, IAdvancedReminderRecoveryGrain
{
    private const int BatchSize = 32;
    private readonly IReminderTable _reminderTable = reminderTable;
    private readonly IGrainFactory _grainFactory = grainFactory;
    private bool _started;

    public async Task StartAsync(bool force, CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        var reminders = (await _reminderTable.ReadRows(0, 0)).Reminders;
        for (var offset = 0; offset < reminders.Count; offset += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(BatchSize, reminders.Count - offset);
            var tasks = new List<Task>(count);
            for (var index = 0; index < count; index++)
            {
                var entry = reminders[offset + index];
                var dispatcher = _grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString());
                tasks.Add(dispatcher.EnsureScheduledAsync(
                    entry.GrainId,
                    entry.ReminderName,
                    entry.ScheduleId,
                    force || string.IsNullOrEmpty(entry.JobId) || string.IsNullOrEmpty(entry.JobShardId),
                    cancellationToken));
            }

            await Task.WhenAll(tasks);
        }

        _started = true;
    }
}
