using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.AdvancedReminders.Timers;
using Orleans.Runtime;

#nullable enable
namespace Orleans.AdvancedReminders;

/// <summary>
/// Extension methods for accessing reminders from a <see cref="Grain"/> or <see cref="IGrainBase"/> implementation.
/// </summary>
public static class GrainReminderExtensions
{
    /// <summary>
    /// Registers a persistent, reliable reminder to send regular notifications (reminders) to the grain.
    /// The grain must implement the <c>Orleans.AdvancedReminders.IRemindable</c> interface, and reminders for this grain will be sent to the <c>ReceiveReminder</c> callback method.
    /// If the current grain is deactivated when the timer fires, a new activation of this grain will be created to receive this reminder.
    /// If an existing reminder with the same name already exists, that reminder will be overwritten with this new reminder.
    /// Reminders will always be received by one activation of this grain, even if multiple activations exist for this grain.
    /// </summary>
    /// <param name="grain">The grain instance.</param>
    /// <param name="reminderName">Name of this reminder</param>
    /// <param name="dueTime">Due time for this reminder</param>
    /// <param name="period">Frequency period for this reminder</param>
    /// <returns>Promise for Reminder handle.</returns>
    public static Task<IGrainReminder> RegisterOrUpdateAdvancedReminder(this Grain grain, string reminderName, TimeSpan dueTime, TimeSpan period)
        => RegisterOrUpdateAdvancedReminder(IsRemindable(grain), grain?.GrainContext, reminderName, dueTime, period);

    /// <summary>
    /// Registers a persistent, reliable reminder to send regular notifications (reminders) to the grain.
    /// The grain must implement the <c>Orleans.AdvancedReminders.IRemindable</c> interface, and reminders for this grain will be sent to the <c>ReceiveReminder</c> callback method.
    /// If the current grain is deactivated when the timer fires, a new activation of this grain will be created to receive this reminder.
    /// If an existing reminder with the same name already exists, that reminder will be overwritten with this new reminder.
    /// Reminders will always be received by one activation of this grain, even if multiple activations exist for this grain.
    /// </summary>
    /// <param name="grain">The grain instance.</param>
    /// <param name="reminderName">Name of this reminder</param>
    /// <param name="dueTime">Due time for this reminder</param>
    /// <param name="period">Frequency period for this reminder</param>
    /// <returns>Promise for Reminder handle.</returns>
    public static Task<IGrainReminder> RegisterOrUpdateAdvancedReminder(this IGrainBase grain, string reminderName, TimeSpan dueTime, TimeSpan period)
        => RegisterOrUpdateAdvancedReminder(IsRemindable(grain), grain?.GrainContext, reminderName, dueTime, period);

    /// <summary>
    /// Registers a persistent, reliable reminder to send regular notifications (reminders) to the grain using an absolute UTC due timestamp.
    /// The grain must implement the <c>Orleans.AdvancedReminders.IRemindable</c> interface, and reminders for this grain will be sent to the <c>ReceiveReminder</c> callback method.
    /// If the current grain is deactivated when the timer fires, a new activation of this grain will be created to receive this reminder.
    /// If an existing reminder with the same name already exists, that reminder will be overwritten with this new reminder.
    /// Reminders will always be received by one activation of this grain, even if multiple activations exist for this grain.
    /// </summary>
    /// <param name="grain">The grain instance.</param>
    /// <param name="reminderName">Name of this reminder</param>
    /// <param name="dueAtUtc">UTC timestamp for this reminder's first tick.</param>
    /// <param name="period">Frequency period for this reminder</param>
    /// <returns>Promise for Reminder handle.</returns>
    public static Task<IGrainReminder> RegisterOrUpdateAdvancedReminder(this Grain grain, string reminderName, DateTime dueAtUtc, TimeSpan period)
        => RegisterOrUpdateAdvancedReminder(IsRemindable(grain), grain?.GrainContext, reminderName, dueAtUtc, period);

    /// <summary>
    /// Registers a persistent, reliable reminder to send regular notifications (reminders) to the grain using an absolute UTC due timestamp.
    /// The grain must implement the <c>Orleans.AdvancedReminders.IRemindable</c> interface, and reminders for this grain will be sent to the <c>ReceiveReminder</c> callback method.
    /// If the current grain is deactivated when the timer fires, a new activation of this grain will be created to receive this reminder.
    /// If an existing reminder with the same name already exists, that reminder will be overwritten with this new reminder.
    /// Reminders will always be received by one activation of this grain, even if multiple activations exist for this grain.
    /// </summary>
    /// <param name="grain">The grain instance.</param>
    /// <param name="reminderName">Name of this reminder</param>
    /// <param name="dueAtUtc">UTC timestamp for this reminder's first tick.</param>
    /// <param name="period">Frequency period for this reminder</param>
    /// <returns>Promise for Reminder handle.</returns>
    public static Task<IGrainReminder> RegisterOrUpdateAdvancedReminder(this IGrainBase grain, string reminderName, DateTime dueAtUtc, TimeSpan period)
        => RegisterOrUpdateAdvancedReminder(IsRemindable(grain), grain?.GrainContext, reminderName, dueAtUtc, period);

    /// <summary>
    /// Registers a persistent, reliable reminder using the provided schedule.
    /// </summary>
    /// <param name="grain">The grain instance.</param>
    /// <param name="reminderName">Name of this reminder.</param>
    /// <param name="schedule">Reminder schedule.</param>
    /// <returns>Promise for Reminder handle.</returns>
    public static Task<IGrainReminder> RegisterOrUpdateAdvancedReminder(this Grain grain, string reminderName, ReminderSchedule schedule)
        => RegisterOrUpdateAdvancedReminder(IsRemindable(grain), grain?.GrainContext, reminderName, schedule, Runtime.ReminderPriority.Normal, Runtime.MissedReminderAction.Skip);

    /// <summary>
    /// Registers a persistent, reliable reminder using the provided schedule.
    /// </summary>
    /// <param name="grain">The grain instance.</param>
    /// <param name="reminderName">Name of this reminder.</param>
    /// <param name="schedule">Reminder schedule.</param>
    /// <returns>Promise for Reminder handle.</returns>
    public static Task<IGrainReminder> RegisterOrUpdateAdvancedReminder(this IGrainBase grain, string reminderName, ReminderSchedule schedule)
        => RegisterOrUpdateAdvancedReminder(IsRemindable(grain), grain?.GrainContext, reminderName, schedule, Runtime.ReminderPriority.Normal, Runtime.MissedReminderAction.Skip);

    /// <summary>
    /// Registers a persistent, reliable reminder to send regular notifications (reminders) to the grain with adaptive delivery options.
    /// </summary>
    /// <param name="grain">The grain instance.</param>
    /// <param name="reminderName">Name of this reminder.</param>
    /// <param name="dueTime">Due time for this reminder.</param>
    /// <param name="period">Frequency period for this reminder.</param>
    /// <param name="priority">Reminder priority.</param>
    /// <param name="action">Missed reminder action.</param>
    /// <returns>Promise for Reminder handle.</returns>
    public static Task<IGrainReminder> RegisterOrUpdateAdvancedReminder(
        this Grain grain,
        string reminderName,
        TimeSpan dueTime,
        TimeSpan period,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateAdvancedReminder(IsRemindable(grain), grain?.GrainContext, reminderName, dueTime, period, priority, action);

    /// <summary>
    /// Registers a persistent, reliable reminder to send regular notifications (reminders) to the grain with adaptive delivery options.
    /// </summary>
    /// <param name="grain">The grain instance.</param>
    /// <param name="reminderName">Name of this reminder.</param>
    /// <param name="dueTime">Due time for this reminder.</param>
    /// <param name="period">Frequency period for this reminder.</param>
    /// <param name="priority">Reminder priority.</param>
    /// <param name="action">Missed reminder action.</param>
    /// <returns>Promise for Reminder handle.</returns>
    public static Task<IGrainReminder> RegisterOrUpdateAdvancedReminder(
        this IGrainBase grain,
        string reminderName,
        TimeSpan dueTime,
        TimeSpan period,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateAdvancedReminder(IsRemindable(grain), grain?.GrainContext, reminderName, dueTime, period, priority, action);

    /// <summary>
    /// Registers a persistent, reliable reminder to send regular notifications (reminders) to the grain using an absolute UTC due timestamp with adaptive delivery options.
    /// </summary>
    /// <param name="grain">The grain instance.</param>
    /// <param name="reminderName">Name of this reminder.</param>
    /// <param name="dueAtUtc">UTC timestamp for this reminder's first tick.</param>
    /// <param name="period">Frequency period for this reminder.</param>
    /// <param name="priority">Reminder priority.</param>
    /// <param name="action">Missed reminder action.</param>
    /// <returns>Promise for Reminder handle.</returns>
    public static Task<IGrainReminder> RegisterOrUpdateAdvancedReminder(
        this Grain grain,
        string reminderName,
        DateTime dueAtUtc,
        TimeSpan period,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateAdvancedReminder(IsRemindable(grain), grain?.GrainContext, reminderName, dueAtUtc, period, priority, action);

    /// <summary>
    /// Registers a persistent, reliable reminder to send regular notifications (reminders) to the grain using an absolute UTC due timestamp with adaptive delivery options.
    /// </summary>
    /// <param name="grain">The grain instance.</param>
    /// <param name="reminderName">Name of this reminder.</param>
    /// <param name="dueAtUtc">UTC timestamp for this reminder's first tick.</param>
    /// <param name="period">Frequency period for this reminder.</param>
    /// <param name="priority">Reminder priority.</param>
    /// <param name="action">Missed reminder action.</param>
    /// <returns>Promise for Reminder handle.</returns>
    public static Task<IGrainReminder> RegisterOrUpdateAdvancedReminder(
        this IGrainBase grain,
        string reminderName,
        DateTime dueAtUtc,
        TimeSpan period,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateAdvancedReminder(IsRemindable(grain), grain?.GrainContext, reminderName, dueAtUtc, period, priority, action);

    /// <summary>
    /// Registers a persistent, reliable reminder using the provided schedule with adaptive delivery options.
    /// </summary>
    /// <param name="grain">The grain instance.</param>
    /// <param name="reminderName">Name of this reminder.</param>
    /// <param name="schedule">Reminder schedule.</param>
    /// <param name="priority">Reminder priority.</param>
    /// <param name="action">Missed reminder action.</param>
    /// <returns>Promise for Reminder handle.</returns>
    public static Task<IGrainReminder> RegisterOrUpdateAdvancedReminder(
        this Grain grain,
        string reminderName,
        ReminderSchedule schedule,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateAdvancedReminder(IsRemindable(grain), grain?.GrainContext, reminderName, schedule, priority, action);

    /// <summary>
    /// Registers a persistent, reliable reminder using the provided schedule with adaptive delivery options.
    /// </summary>
    /// <param name="grain">The grain instance.</param>
    /// <param name="reminderName">Name of this reminder.</param>
    /// <param name="schedule">Reminder schedule.</param>
    /// <param name="priority">Reminder priority.</param>
    /// <param name="action">Missed reminder action.</param>
    /// <returns>Promise for Reminder handle.</returns>
    public static Task<IGrainReminder> RegisterOrUpdateAdvancedReminder(
        this IGrainBase grain,
        string reminderName,
        ReminderSchedule schedule,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateAdvancedReminder(IsRemindable(grain), grain?.GrainContext, reminderName, schedule, priority, action);

    private static Task<IGrainReminder> RegisterOrUpdateAdvancedReminder(bool remindable, IGrainContext? grainContext, string reminderName, TimeSpan dueTime, TimeSpan period)
        => RegisterOrUpdateAdvancedReminder(
            remindable,
            grainContext,
            reminderName,
            ReminderSchedule.Interval(dueTime, period),
            Runtime.ReminderPriority.Normal,
            Runtime.MissedReminderAction.Skip);

    private static Task<IGrainReminder> RegisterOrUpdateAdvancedReminder(bool remindable, IGrainContext? grainContext, string reminderName, DateTime dueAtUtc, TimeSpan period)
        => RegisterOrUpdateAdvancedReminder(
            remindable,
            grainContext,
            reminderName,
            ReminderSchedule.Interval(dueAtUtc, period),
            Runtime.ReminderPriority.Normal,
            Runtime.MissedReminderAction.Skip);

    private static Task<IGrainReminder> RegisterOrUpdateAdvancedReminder(
        bool remindable,
        IGrainContext? grainContext,
        string reminderName,
        TimeSpan dueTime,
        TimeSpan period,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateAdvancedReminder(
            remindable,
            grainContext,
            reminderName,
            ReminderSchedule.Interval(dueTime, period),
            priority,
            action);

    private static Task<IGrainReminder> RegisterOrUpdateAdvancedReminder(
        bool remindable,
        IGrainContext? grainContext,
        string reminderName,
        DateTime dueAtUtc,
        TimeSpan period,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateAdvancedReminder(
            remindable,
            grainContext,
            reminderName,
            ReminderSchedule.Interval(dueAtUtc, period),
            priority,
            action);

    private static Task<IGrainReminder> RegisterOrUpdateAdvancedReminder(
        bool remindable,
        IGrainContext? grainContext,
        string reminderName,
        ReminderSchedule schedule,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
    {
        ArgumentNullException.ThrowIfNull(grainContext, "grain");
        ArgumentNullException.ThrowIfNull(schedule);
        if (string.IsNullOrWhiteSpace(reminderName)) throw new ArgumentNullException(nameof(reminderName));
        if (!remindable) throw new InvalidOperationException($"Grain {grainContext.GrainId} is not '{typeof(IRemindable).FullName}'. A grain should implement {typeof(IRemindable).FullName} to use the advanced reminder service.");

        return GetReminderRegistry(grainContext).RegisterOrUpdateReminder(grainContext.GrainId, reminderName, schedule, priority, action);
    }

    /// <summary>
    /// Unregisters a previously registered reminder.
    /// </summary>
    /// <param name="grain">The grain instance.</param>
    /// <param name="reminder">Reminder to unregister.</param>
    /// <returns>Completion promise for this operation.</returns>
    public static Task UnregisterAdvancedReminder(this Grain grain, IGrainReminder reminder) => UnregisterAdvancedReminder(grain?.GrainContext, reminder);

    /// <summary>
    /// Unregisters a previously registered reminder.
    /// </summary>
    /// <param name="grain">The grain instance.</param>
    /// <param name="reminder">Reminder to unregister.</param>
    /// <returns>Completion promise for this operation.</returns>
    public static Task UnregisterAdvancedReminder(this IGrainBase grain, IGrainReminder reminder) => UnregisterAdvancedReminder(grain?.GrainContext, reminder);

    private static Task UnregisterAdvancedReminder(IGrainContext? grainContext, IGrainReminder reminder)
    {
        ArgumentNullException.ThrowIfNull(grainContext, "grain");
        return GetReminderRegistry(grainContext).UnregisterReminder(grainContext.GrainId, reminder);
    }

    /// <summary>
    /// Returns a previously registered reminder.
    /// </summary>
    /// <param name="grain">The grain instance.</param>
    /// <param name="reminderName">Reminder to return</param>
    /// <returns>Promise for Reminder handle.</returns>
    public static Task<IGrainReminder?> GetAdvancedReminder(this Grain grain, string reminderName) => GetAdvancedReminder(grain?.GrainContext, reminderName);

    /// <summary>
    /// Returns a previously registered reminder.
    /// </summary>
    /// <param name="grain">A grain.</param>
    /// <param name="reminderName">Reminder to return</param>
    /// <returns>Promise for Reminder handle.</returns>
    public static Task<IGrainReminder?> GetAdvancedReminder(this IGrainBase grain, string reminderName) => GetAdvancedReminder(grain?.GrainContext, reminderName);

    private static Task<IGrainReminder?> GetAdvancedReminder(IGrainContext? grainContext, string reminderName)
    {
        ArgumentNullException.ThrowIfNull(grainContext, "grain");
        if (string.IsNullOrWhiteSpace(reminderName)) throw new ArgumentNullException(nameof(reminderName));

        return GetReminderRegistry(grainContext).GetReminder(grainContext.GrainId, reminderName);
    }

    /// <summary>
    /// Returns a list of all reminders registered by the grain.
    /// </summary>
    /// <returns>Promise for list of Reminders registered for this grain.</returns>
    public static Task<List<IGrainReminder>> GetAdvancedReminders(this Grain grain) => GetAdvancedReminders(grain?.GrainContext);

    /// <summary>
    /// Returns a list of all reminders registered by the grain.
    /// </summary>
    /// <returns>Promise for list of Reminders registered for this grain.</returns>
    public static Task<List<IGrainReminder>> GetAdvancedReminders(this IGrainBase grain) => GetAdvancedReminders(grain?.GrainContext);

    private static Task<List<IGrainReminder>> GetAdvancedReminders(IGrainContext? grainContext)
    {
        ArgumentNullException.ThrowIfNull(grainContext, "grain");
        return GetReminderRegistry(grainContext).GetReminders(grainContext.GrainId);
    }

    /// <summary>
    /// Gets the <see cref="IReminderRegistry"/>.
    /// </summary>
    private static IReminderRegistry GetReminderRegistry(IGrainContext grainContext)
    {
        return grainContext.ActivationServices.GetRequiredService<IReminderRegistry>();
    }

    private static bool IsRemindable(object? grain) => grain is IRemindable;
}
