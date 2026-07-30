using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.AdvancedReminders.Cron.Internal;
using Orleans.AdvancedReminders.Timers;
using Orleans.DurableJobs;
using Orleans.Runtime;

namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal sealed class ReminderRegistry(
    IServiceProvider serviceProvider,
    IOptions<ReminderOptions> options,
    [FromKeyedServices(DurableJobTimeProviderNames.DurableJobs)] TimeProvider timeProvider) : IReminderRegistry
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ReminderOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider;

    public Task<IGrainReminder> RegisterOrUpdateReminder(
        GrainId callingGrainId,
        string reminderName,
        ReminderSchedule schedule,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ReminderValidation.Validate(_options, reminderName, schedule, priority, action, _timeProvider.GetUtcNow().UtcDateTime);
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

}

internal static class ReminderValidation
{
    private const int MaxCronIntervalsToValidate = 10_000;

    public static void Validate(
        ReminderOptions options,
        string reminderName,
        ReminderSchedule schedule,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action,
        DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(reminderName))
        {
            throw new ArgumentException("Cannot use null or empty name for the reminder", nameof(reminderName));
        }

        ValidatePriorityAndAction(priority, action);

        switch (schedule.Kind)
        {
            case Runtime.ReminderScheduleKind.Interval:
                ValidateIntervalSchedule(options, schedule, reminderName, utcNow);
                break;
            case Runtime.ReminderScheduleKind.Cron:
                ValidateCronSchedule(options, schedule, reminderName, utcNow);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(schedule), schedule.Kind, "Unsupported reminder schedule kind.");
        }
    }

    private static void ValidateIntervalSchedule(
        ReminderOptions options,
        ReminderSchedule schedule,
        string reminderName,
        DateTime utcNow)
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

            if (dueTime > DateTime.MaxValue - utcNow)
            {
                throw new ArgumentOutOfRangeException(nameof(schedule), "The due time exceeds the supported date range");
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

        if (!schedule.IsOneShot && period < options.MinimumReminderPeriod)
        {
            throw new ArgumentException(
                $"Cannot register reminder {reminderName} as requested period ({period}) is less than minimum allowed reminder period ({options.MinimumReminderPeriod})");
        }
    }

    private static void ValidateCronSchedule(ReminderOptions options, ReminderSchedule schedule, string reminderName, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(schedule.CronExpression))
        {
            throw new ArgumentException("Cannot use null or empty cron expression for the reminder", nameof(schedule));
        }

        var cron = ReminderCronSchedule.Parse(schedule.CronExpression, schedule.CronTimeZoneId);
        var precision = ReminderCronParser.DetectFormat(schedule.CronExpression) == CronFormat.IncludeSeconds
            ? TimeSpan.FromSeconds(1)
            : TimeSpan.FromMinutes(1);
        if (options.MinimumReminderPeriod <= precision)
        {
            return;
        }

        var previous = cron.GetNextOccurrence(utcNow, inclusive: true);
        for (var index = 0; index < MaxCronIntervalsToValidate && previous is not null; index++)
        {
            var next = cron.GetNextOccurrence(previous.Value);
            if (next is null)
            {
                return;
            }

            var interval = next.Value - previous.Value;
            if (interval < options.MinimumReminderPeriod)
            {
                throw new ArgumentException(
                    $"Cannot register reminder {reminderName} because its cron interval ({interval}) is less than minimum allowed reminder period ({options.MinimumReminderPeriod})",
                    nameof(schedule));
            }

            previous = next;
        }
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
