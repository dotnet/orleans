using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.DurableReminders.Cron.Internal;
using Orleans.DurableReminders.Timers;
using Orleans.Runtime;

namespace Orleans.DurableReminders.Runtime.ReminderService;

internal sealed class ReminderRegistry(IServiceProvider serviceProvider, IOptions<ReminderOptions> options) : IReminderRegistry
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ReminderOptions _options = options.Value;

    public Task<IGrainReminder> RegisterOrUpdateReminder(
        GrainId callingGrainId,
        string reminderName,
        ReminderSchedule schedule,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ValidateSchedule(reminderName, schedule, priority, action);
        return GetReminderService().RegisterOrUpdateReminder(callingGrainId, reminderName, schedule, priority, action);
    }

    public Task UnregisterReminder(GrainId callingGrainId, IGrainReminder reminder)
    {
        ArgumentNullException.ThrowIfNull(reminder);
        return GetReminderService().UnregisterReminder(reminder);
    }

    public Task<IGrainReminder?> GetReminder(GrainId callingGrainId, string reminderName)
    {
        if (string.IsNullOrWhiteSpace(reminderName))
        {
            throw new ArgumentException("Cannot use null or empty name for the reminder", nameof(reminderName));
        }

        return GetReminderService().GetReminder(callingGrainId, reminderName);
    }

    public Task<List<IGrainReminder>> GetReminders(GrainId callingGrainId) => GetReminderService().GetReminders(callingGrainId);

    private IReminderService GetReminderService()
        => _serviceProvider.GetRequiredService<IReminderService>();

    private void ValidateSchedule(string reminderName, ReminderSchedule schedule, Runtime.ReminderPriority priority, Runtime.MissedReminderAction action)
    {
        if (string.IsNullOrWhiteSpace(reminderName))
        {
            throw new ArgumentException("Cannot use null or empty name for the reminder", nameof(reminderName));
        }

        ValidatePriorityAndAction(priority, action);

        switch (schedule.Kind)
        {
            case Runtime.ReminderScheduleKind.Interval:
                ValidateIntervalSchedule(schedule, reminderName);
                break;
            case Runtime.ReminderScheduleKind.Cron:
                ValidateCronSchedule(schedule);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(schedule), schedule.Kind, "Unsupported reminder schedule kind.");
        }
    }

    private void ValidateIntervalSchedule(ReminderSchedule schedule, string reminderName)
    {
        if (schedule.Period is not { } period)
        {
            throw new ArgumentException("Interval reminder schedule must define a period.", nameof(schedule));
        }

        if (schedule.DueTime is { } dueTime)
        {
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(nameof(schedule), "Cannot use InfiniteTimeSpan dueTime to create a reminder");
            }

            if (dueTime.Ticks < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(schedule), "Cannot use negative dueTime to create a reminder");
            }
        }
        else if (schedule.DueAtUtc is { } dueAtUtc)
        {
            if (dueAtUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("Due timestamp must use DateTimeKind.Utc.", nameof(schedule));
            }
        }
        else
        {
            throw new ArgumentException("Interval reminder schedule must define dueTime or dueAtUtc.", nameof(schedule));
        }

        if (period == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(schedule), "Cannot use InfiniteTimeSpan period to create a reminder");
        }

        if (period.Ticks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schedule), "Cannot use negative period to create a reminder");
        }

        if (period < _options.MinimumReminderPeriod)
        {
            throw new ArgumentException(
                $"Cannot register reminder {reminderName} as requested period ({period}) is less than minimum allowed reminder period ({_options.MinimumReminderPeriod})");
        }
    }

    private static void ValidateCronSchedule(ReminderSchedule schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule.CronExpression))
        {
            throw new ArgumentException("Cannot use null or empty cron expression for the reminder", nameof(schedule));
        }

        _ = ReminderCronSchedule.Parse(schedule.CronExpression, schedule.CronTimeZoneId);
    }

    private static void ValidatePriorityAndAction(Runtime.ReminderPriority priority, Runtime.MissedReminderAction action)
    {
        if (!Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(nameof(priority), priority, "Invalid reminder priority.");
        }

        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "Invalid missed reminder action.");
        }
    }
}
