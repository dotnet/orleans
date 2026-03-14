using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.AdvancedReminders.Cron.Internal;
using Orleans.Runtime;
using Orleans.AdvancedReminders.Timers;

#nullable enable
namespace Orleans.AdvancedReminders;

/// <summary>
/// Extension methods for registering cron-based Orleans reminders.
/// </summary>
public static class GrainReminderCronExtensions
{
    /// <summary>
    /// Registers or updates a persistent cron reminder.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(this Grain grain, string reminderName, string cronExpression)
        => RegisterOrUpdateReminder(IsRemindable(grain), grain?.GrainContext, reminderName, cronExpression);

    /// <summary>
    /// Registers or updates a persistent cron reminder.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(this IGrainBase grain, string reminderName, string cronExpression)
        => RegisterOrUpdateReminder(IsRemindable(grain), grain?.GrainContext, reminderName, cronExpression);

    /// <summary>
    /// Registers or updates a persistent cron reminder.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(this Grain grain, string reminderName, ReminderCronExpression cronExpression)
        => RegisterOrUpdateReminder(grain, reminderName, cronExpression, timeZone: null);

    /// <summary>
    /// Registers or updates a persistent cron reminder.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(this IGrainBase grain, string reminderName, ReminderCronExpression cronExpression)
        => RegisterOrUpdateReminder(grain, reminderName, cronExpression, timeZone: null);

    /// <summary>
    /// Registers or updates a persistent cron reminder.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this Grain grain,
        string reminderName,
        ReminderCronExpression cronExpression,
        TimeZoneInfo? timeZone)
        => RegisterOrUpdateReminder(
            IsRemindable(grain),
            grain?.GrainContext,
            reminderName,
            cronExpression?.ToExpressionString(),
            GetCronTimeZoneId(timeZone));

    /// <summary>
    /// Registers or updates a persistent cron reminder.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IGrainBase grain,
        string reminderName,
        ReminderCronExpression cronExpression,
        TimeZoneInfo? timeZone)
        => RegisterOrUpdateReminder(
            IsRemindable(grain),
            grain?.GrainContext,
            reminderName,
            cronExpression?.ToExpressionString(),
            GetCronTimeZoneId(timeZone));

    /// <summary>
    /// Registers or updates a persistent cron reminder.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(this Grain grain, string reminderName, ReminderCronBuilder cronBuilder)
        => RegisterOrUpdateReminder(
            IsRemindable(grain),
            grain?.GrainContext,
            reminderName,
            cronBuilder?.ToExpressionString(),
            GetCronTimeZoneId(cronBuilder?.TimeZone));

    /// <summary>
    /// Registers or updates a persistent cron reminder.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(this IGrainBase grain, string reminderName, ReminderCronBuilder cronBuilder)
        => RegisterOrUpdateReminder(
            IsRemindable(grain),
            grain?.GrainContext,
            reminderName,
            cronBuilder?.ToExpressionString(),
            GetCronTimeZoneId(cronBuilder?.TimeZone));

    /// <summary>
    /// Registers or updates a persistent cron reminder using the provided time zone.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this Grain grain,
        string reminderName,
        string cronExpression,
        TimeZoneInfo? timeZone)
        => RegisterOrUpdateReminder(
            IsRemindable(grain),
            grain?.GrainContext,
            reminderName,
            cronExpression,
            GetCronTimeZoneId(timeZone));

    /// <summary>
    /// Registers or updates a persistent cron reminder using the provided time zone.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IGrainBase grain,
        string reminderName,
        string cronExpression,
        TimeZoneInfo? timeZone)
        => RegisterOrUpdateReminder(
            IsRemindable(grain),
            grain?.GrainContext,
            reminderName,
            cronExpression,
            GetCronTimeZoneId(timeZone));

    /// <summary>
    /// Registers or updates a persistent cron reminder with adaptive delivery options.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this Grain grain,
        string reminderName,
        string cronExpression,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateReminder(grain, reminderName, cronExpression, priority, action, timeZone: null);

    /// <summary>
    /// Registers or updates a persistent cron reminder with adaptive delivery options.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IGrainBase grain,
        string reminderName,
        string cronExpression,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateReminder(grain, reminderName, cronExpression, priority, action, timeZone: null);

    /// <summary>
    /// Registers or updates a persistent cron reminder with adaptive delivery options.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this Grain grain,
        string reminderName,
        string cronExpression,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action,
        TimeZoneInfo? timeZone)
        => RegisterOrUpdateReminder(
            IsRemindable(grain),
            grain?.GrainContext,
            reminderName,
            cronExpression,
            GetCronTimeZoneId(timeZone),
            priority,
            action);

    /// <summary>
    /// Registers or updates a persistent cron reminder with adaptive delivery options.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IGrainBase grain,
        string reminderName,
        string cronExpression,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action,
        TimeZoneInfo? timeZone)
        => RegisterOrUpdateReminder(
            IsRemindable(grain),
            grain?.GrainContext,
            reminderName,
            cronExpression,
            GetCronTimeZoneId(timeZone),
            priority,
            action);

    /// <summary>
    /// Registers or updates a persistent cron reminder with adaptive delivery options.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this Grain grain,
        string reminderName,
        ReminderCronExpression cronExpression,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateReminder(grain, reminderName, cronExpression, priority, action, timeZone: null);

    /// <summary>
    /// Registers or updates a persistent cron reminder with adaptive delivery options.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IGrainBase grain,
        string reminderName,
        ReminderCronExpression cronExpression,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateReminder(grain, reminderName, cronExpression, priority, action, timeZone: null);

    /// <summary>
    /// Registers or updates a persistent cron reminder with adaptive delivery options.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this Grain grain,
        string reminderName,
        ReminderCronExpression cronExpression,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action,
        TimeZoneInfo? timeZone)
        => RegisterOrUpdateReminder(
            IsRemindable(grain),
            grain?.GrainContext,
            reminderName,
            cronExpression?.ToExpressionString(),
            GetCronTimeZoneId(timeZone),
            priority,
            action);

    /// <summary>
    /// Registers or updates a persistent cron reminder with adaptive delivery options.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IGrainBase grain,
        string reminderName,
        ReminderCronExpression cronExpression,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action,
        TimeZoneInfo? timeZone)
        => RegisterOrUpdateReminder(
            IsRemindable(grain),
            grain?.GrainContext,
            reminderName,
            cronExpression?.ToExpressionString(),
            GetCronTimeZoneId(timeZone),
            priority,
            action);

    /// <summary>
    /// Registers or updates a persistent cron reminder with adaptive delivery options.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this Grain grain,
        string reminderName,
        ReminderCronBuilder cronBuilder,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateReminder(
            IsRemindable(grain),
            grain?.GrainContext,
            reminderName,
            cronBuilder?.ToExpressionString(),
            GetCronTimeZoneId(cronBuilder?.TimeZone),
            priority,
            action);

    /// <summary>
    /// Registers or updates a persistent cron reminder with adaptive delivery options.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IGrainBase grain,
        string reminderName,
        ReminderCronBuilder cronBuilder,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateReminder(
            IsRemindable(grain),
            grain?.GrainContext,
            reminderName,
            cronBuilder?.ToExpressionString(),
            GetCronTimeZoneId(cronBuilder?.TimeZone),
            priority,
            action);

    /// <summary>
    /// Registers or updates a persistent cron reminder using the provided time zone and adaptive delivery options.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this Grain grain,
        string reminderName,
        string cronExpression,
        TimeZoneInfo? timeZone,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateReminder(grain, reminderName, cronExpression, priority, action, timeZone);

    /// <summary>
    /// Registers or updates a persistent cron reminder using the provided time zone and adaptive delivery options.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IGrainBase grain,
        string reminderName,
        string cronExpression,
        TimeZoneInfo? timeZone,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateReminder(grain, reminderName, cronExpression, priority, action, timeZone);

    private static Task<IGrainReminder> RegisterOrUpdateReminder(
        bool remindable,
        IGrainContext? grainContext,
        string reminderName,
        string? cronExpression,
        string? cronTimeZoneId = null)
        => RegisterOrUpdateReminder(
            remindable,
            grainContext,
            reminderName,
            cronExpression,
            cronTimeZoneId,
            Runtime.ReminderPriority.Normal,
            Runtime.MissedReminderAction.Skip);

    private static Task<IGrainReminder> RegisterOrUpdateReminder(
        bool remindable,
        IGrainContext? grainContext,
        string reminderName,
        string? cronExpression,
        string? cronTimeZoneId,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
    {
        ArgumentNullException.ThrowIfNull(grainContext, "grain");
        if (string.IsNullOrWhiteSpace(reminderName)) throw new ArgumentNullException(nameof(reminderName));
        if (string.IsNullOrWhiteSpace(cronExpression)) throw new ArgumentNullException(nameof(cronExpression));
        if (!remindable)
        {
            throw new InvalidOperationException(
                $"Grain {grainContext.GrainId} is not '{typeof(IRemindable).FullName}'. A grain should implement {typeof(IRemindable).FullName} to use the persistent durable reminder service.");
        }

        return grainContext.ActivationServices.GetRequiredService<IReminderRegistry>()
            .RegisterOrUpdateReminder(
                grainContext.GrainId,
                reminderName,
                ReminderSchedule.Cron(cronExpression, cronTimeZoneId),
                priority,
                action);
    }

    private static string? GetCronTimeZoneId(TimeZoneInfo? timeZone)
        => ReminderCronSchedule.NormalizeTimeZoneIdForStorage(timeZone);

    private static bool IsRemindable(object? grain) => grain is IRemindable;
}
