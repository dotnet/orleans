using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Timers;

#nullable enable
namespace Orleans;

/// <summary>
/// Extension methods for registering cron-based Orleans reminders.
/// </summary>
public static class GrainReminderCronExtensions
{
    /// <summary>
    /// Registers or updates a persistent cron reminder.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(this Grain grain, string reminderName, string cronExpression)
        => RegisterOrUpdateReminder(grain is IRemindable, grain?.GrainContext, reminderName, cronExpression);

    /// <summary>
    /// Registers or updates a persistent cron reminder.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(this IGrainBase grain, string reminderName, string cronExpression)
        => RegisterOrUpdateReminder(grain is IRemindable, grain?.GrainContext, reminderName, cronExpression);

    /// <summary>
    /// Registers or updates a persistent cron reminder.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(this Grain grain, string reminderName, ReminderCronExpression cronExpression)
        => RegisterOrUpdateReminder(grain is IRemindable, grain?.GrainContext, reminderName, cronExpression?.ToExpressionString());

    /// <summary>
    /// Registers or updates a persistent cron reminder.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(this IGrainBase grain, string reminderName, ReminderCronExpression cronExpression)
        => RegisterOrUpdateReminder(grain is IRemindable, grain?.GrainContext, reminderName, cronExpression?.ToExpressionString());

    /// <summary>
    /// Registers or updates a persistent cron reminder.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(this Grain grain, string reminderName, ReminderCronBuilder cronBuilder)
        => RegisterOrUpdateReminder(grain is IRemindable, grain?.GrainContext, reminderName, cronBuilder?.ToExpressionString());

    /// <summary>
    /// Registers or updates a persistent cron reminder.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(this IGrainBase grain, string reminderName, ReminderCronBuilder cronBuilder)
        => RegisterOrUpdateReminder(grain is IRemindable, grain?.GrainContext, reminderName, cronBuilder?.ToExpressionString());

    /// <summary>
    /// Registers or updates a persistent cron reminder with adaptive delivery options.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this Grain grain,
        string reminderName,
        string cronExpression,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateReminder(grain is IRemindable, grain?.GrainContext, reminderName, cronExpression, priority, action);

    /// <summary>
    /// Registers or updates a persistent cron reminder with adaptive delivery options.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IGrainBase grain,
        string reminderName,
        string cronExpression,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateReminder(grain is IRemindable, grain?.GrainContext, reminderName, cronExpression, priority, action);

    /// <summary>
    /// Registers or updates a persistent cron reminder with adaptive delivery options.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this Grain grain,
        string reminderName,
        ReminderCronExpression cronExpression,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateReminder(grain is IRemindable, grain?.GrainContext, reminderName, cronExpression?.ToExpressionString(), priority, action);

    /// <summary>
    /// Registers or updates a persistent cron reminder with adaptive delivery options.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IGrainBase grain,
        string reminderName,
        ReminderCronExpression cronExpression,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateReminder(grain is IRemindable, grain?.GrainContext, reminderName, cronExpression?.ToExpressionString(), priority, action);

    /// <summary>
    /// Registers or updates a persistent cron reminder with adaptive delivery options.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this Grain grain,
        string reminderName,
        ReminderCronBuilder cronBuilder,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateReminder(grain is IRemindable, grain?.GrainContext, reminderName, cronBuilder?.ToExpressionString(), priority, action);

    /// <summary>
    /// Registers or updates a persistent cron reminder with adaptive delivery options.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IGrainBase grain,
        string reminderName,
        ReminderCronBuilder cronBuilder,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateReminder(grain is IRemindable, grain?.GrainContext, reminderName, cronBuilder?.ToExpressionString(), priority, action);

    private static Task<IGrainReminder> RegisterOrUpdateReminder(bool remindable, IGrainContext? grainContext, string reminderName, string? cronExpression)
        => RegisterOrUpdateReminder(remindable, grainContext, reminderName, cronExpression, Runtime.ReminderPriority.Normal, Runtime.MissedReminderAction.Skip);

    private static Task<IGrainReminder> RegisterOrUpdateReminder(
        bool remindable,
        IGrainContext? grainContext,
        string reminderName,
        string? cronExpression,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
    {
        ArgumentNullException.ThrowIfNull(grainContext, "grain");
        if (string.IsNullOrWhiteSpace(reminderName)) throw new ArgumentNullException(nameof(reminderName));
        if (string.IsNullOrWhiteSpace(cronExpression)) throw new ArgumentNullException(nameof(cronExpression));
        if (!remindable)
        {
            throw new InvalidOperationException(
                $"Grain {grainContext.GrainId} is not '{nameof(IRemindable)}'. A grain should implement {nameof(IRemindable)} to use the persistent reminder service");
        }

        return grainContext.ActivationServices.GetRequiredService<IReminderRegistry>()
            .RegisterOrUpdateReminder(grainContext.GrainId, reminderName, cronExpression, priority, action);
    }
}
