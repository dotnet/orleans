using System;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Dashboard.Core;
using Orleans.Dashboard.Model;
using AdvancedReminderEntry = Orleans.AdvancedReminders.ReminderEntry;
using AdvancedReminderTable = Orleans.AdvancedReminders.IReminderTable;
using ClassicReminderEntry = Orleans.ReminderEntry;
using ClassicReminderTable = Orleans.IReminderTable;

#nullable disable
namespace Orleans.Dashboard.Implementation.Grains;

internal sealed class DashboardRemindersGrain : Grain, IDashboardRemindersGrain
{
    private static readonly Immutable<ReminderResponse> EmptyReminders = new ReminderResponse
    {
        Reminders = []
    }.AsImmutable();

    private static readonly Immutable<AdvancedReminderResponse> EmptyAdvancedReminders = new AdvancedReminderResponse
    {
        Reminders = []
    }.AsImmutable();

    private readonly AdvancedReminderTable _advancedReminderTable;
    private readonly ClassicReminderTable _classicReminderTable;

    public DashboardRemindersGrain(IServiceProvider serviceProvider)
    {
        _advancedReminderTable = serviceProvider.GetService(typeof(AdvancedReminderTable)) as AdvancedReminderTable;
        _classicReminderTable = serviceProvider.GetService(typeof(ClassicReminderTable)) as ClassicReminderTable;
    }

    public async Task<Immutable<ReminderResponse>> GetReminders(int pageNumber, int pageSize)
    {
        if (_classicReminderTable == null)
        {
            return EmptyReminders;
        }

        var reminderData = await _classicReminderTable.ReadRows(0, 0xffffffff);

        if (!reminderData.Reminders.Any())
        {
            return EmptyReminders;
        }

        return new ReminderResponse
        {
            Reminders = reminderData
                .Reminders
                .OrderBy(x => x.StartAt)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .Select(ToReminderInfo)
                .ToArray(),

            Count = reminderData.Reminders.Count
        }.AsImmutable();
    }

    public async Task<Immutable<AdvancedReminderResponse>> GetAdvancedReminders(int pageNumber, int pageSize)
    {
        if (_advancedReminderTable == null)
        {
            return EmptyAdvancedReminders;
        }

        var reminderData = await _advancedReminderTable.ReadRows(0, 0);

        if (!reminderData.Reminders.Any())
        {
            return EmptyAdvancedReminders;
        }

        return new AdvancedReminderResponse
        {
            Reminders = reminderData
                .Reminders
                .OrderBy(x => x.NextDueUtc ?? x.StartAt)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .Select(ToAdvancedReminderInfo)
                .ToArray(),

            Count = reminderData.Reminders.Count
        }.AsImmutable();
    }

    private static ReminderInfo ToReminderInfo(ClassicReminderEntry entry)
    {
        return new ReminderInfo
        {
            PrimaryKey = entry.GrainId.Key.ToString(),
            GrainReference = entry.GrainId.ToString(),
            Name = entry.ReminderName,
            StartAt = entry.StartAt,
            Period = entry.Period,
        };
    }

    private static AdvancedReminderInfo ToAdvancedReminderInfo(AdvancedReminderEntry entry)
    {
        return new AdvancedReminderInfo
        {
            PrimaryKey = entry.GrainId.Key.ToString(),
            GrainReference = entry.GrainId.ToString(),
            Name = entry.ReminderName,
            StartAt = entry.StartAt,
            Period = entry.Period,
            CronExpression = entry.CronExpression,
            CronTimeZoneId = entry.CronTimeZoneId,
            NextDueUtc = entry.NextDueUtc,
            LastFireUtc = entry.LastFireUtc,
            Priority = entry.Priority.ToString(),
            MissedAction = entry.Action.ToString(),
        };
    }
}
