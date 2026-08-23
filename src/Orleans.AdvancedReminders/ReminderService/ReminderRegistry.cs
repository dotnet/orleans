using System;
using System.Collections.Concurrent;
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
        DurableJobPriority priority,
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
    private const int MaxCronExpressionLength = 200;
    private const int MaxCronIntervalsToValidate = 10_000;
    internal const int MaxCronValidationCacheEntries = 1_024;
    private static readonly ConcurrentDictionary<CronValidationCacheKey, Lazy<bool>> CronValidationCache = new();
    private static readonly ConcurrentQueue<CronValidationCacheKey> CronValidationInsertionOrder = new();
    internal static int CronValidationCacheCount => CronValidationCache.Count;

    public static void Validate(
        ReminderOptions options,
        string reminderName,
        ReminderSchedule schedule,
        DurableJobPriority priority,
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

        if (!schedule.IsOneShot && period == TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(schedule), "Recurring interval reminders require a positive period");
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

        var expression = schedule.CronExpression.Trim();
        if (expression.Length > MaxCronExpressionLength)
        {
            throw new ArgumentException(
                $"Cannot register reminder {reminderName} because its cron expression exceeds {MaxCronExpressionLength} characters",
                nameof(schedule));
        }

        var precision = expression.AsSpan() is ['@', ..]
            || ReminderCronParser.DetectFormat(expression) == CronFormat.IncludeSeconds
            ? TimeSpan.FromSeconds(1)
            : TimeSpan.FromMinutes(1);
        if (options.MinimumReminderPeriod <= precision)
        {
            return;
        }

        var cacheKey = new CronValidationCacheKey(
            expression,
            string.IsNullOrWhiteSpace(schedule.CronTimeZoneId) ? null : schedule.CronTimeZoneId.Trim(),
            options.MinimumReminderPeriod,
            utcNow.Date);
        if (CronValidationCache.TryGetValue(cacheKey, out var cachedValidation))
        {
            _ = cachedValidation.Value;
            return;
        }

        var createdValidation = new Lazy<bool>(
            () =>
            {
                ValidateCronIntervals(options, schedule, reminderName, utcNow);
                return true;
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
        var validation = CronValidationCache.GetOrAdd(cacheKey, createdValidation);
        try
        {
            _ = validation.Value;
        }
        catch
        {
            // Invalid schedules are not cached. Remove only this exact Lazy instance so a
            // concurrent successful replacement cannot be removed by a late observer.
            ((ICollection<KeyValuePair<CronValidationCacheKey, Lazy<bool>>>)CronValidationCache)
                .Remove(new(cacheKey, validation));
            throw;
        }

        if (ReferenceEquals(validation, createdValidation))
        {
            CronValidationInsertionOrder.Enqueue(cacheKey);
            while (CronValidationCache.Count > MaxCronValidationCacheEntries
                && CronValidationInsertionOrder.TryDequeue(out var oldest))
            {
                CronValidationCache.TryRemove(oldest, out _);
            }
        }
    }

    private static void ValidateCronIntervals(ReminderOptions options, ReminderSchedule schedule, string reminderName, DateTime utcNow)
    {
        var cron = ReminderCronSchedule.Parse(schedule.CronExpression!, schedule.CronTimeZoneId);

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

    internal static void ClearCronValidationCache()
    {
        CronValidationCache.Clear();
        while (CronValidationInsertionOrder.TryDequeue(out _))
        {
        }
    }

    private static void ValidatePriorityAndAction(DurableJobPriority priority, Runtime.MissedReminderAction action)
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

    private readonly record struct CronValidationCacheKey(
        string Expression,
        string? TimeZoneId,
        TimeSpan MinimumPeriod,
        DateTime ValidationDateUtc);
}
