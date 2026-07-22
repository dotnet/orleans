#nullable enable

using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains;

internal sealed class AdvancedReminderTestGrain(Orleans.AdvancedReminders.IReminderTable reminderTable)
    : Grain, IAdvancedReminderTestGrain, Orleans.AdvancedReminders.IRemindable
{
    private readonly Orleans.AdvancedReminders.IReminderTable _reminderTable = reminderTable;
    private int _tickCount;

    public async Task Register(string name, TimeSpan dueTime, TimeSpan period)
    {
        await this.RegisterOrUpdateAdvancedReminder(
            name,
            ReminderSchedule.Interval(dueTime, period),
            ReminderPriority.Normal,
            MissedReminderAction.FireImmediately);
    }

    public async Task Unregister(string name)
    {
        var reminder = await this.GetAdvancedReminder(name);
        if (reminder is not null)
        {
            await this.UnregisterAdvancedReminder(reminder);
        }
    }

    public async Task<bool> Exists(string name) => await this.GetAdvancedReminder(name) is not null;

    public Task<int> GetTickCount() => Task.FromResult(_tickCount);

    public Task<string> UpsertRaw(string name, string eTag)
        => _reminderTable.UpsertRow(new Orleans.AdvancedReminders.ReminderEntry
        {
            GrainId = this.GetGrainId(),
            ReminderName = name,
            StartAt = DateTime.UtcNow,
            Period = TimeSpan.FromMinutes(1),
            ETag = eTag,
        });

    public async Task<string?> ReadRawETag(string name)
        => (await _reminderTable.ReadRow(this.GetGrainId(), name))?.ETag;

    public async Task<int> ReadRawGrainCount()
        => (await _reminderTable.ReadRows(this.GetGrainId())).Reminders.Count;

    public async Task<int> ReadRawContainingRangeCount()
    {
        var hash = this.GetGrainId().GetUniformHashCode();
        return (await _reminderTable.ReadRows(unchecked(hash - 1), hash)).Reminders.Count;
    }

    public Task<bool> RemoveRaw(string name, string eTag)
        => _reminderTable.RemoveRow(this.GetGrainId(), name, eTag);

    public Task ClearRawTable() => _reminderTable.TestOnlyClearTable();

    public Task ReceiveReminder(string reminderName, Orleans.AdvancedReminders.Runtime.TickStatus status)
    {
        _tickCount++;
        return Task.CompletedTask;
    }
}
