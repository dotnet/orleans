#nullable enable
using System;
using System.Threading.Tasks;
using Orleans.AdvancedReminders.Timers;
using Orleans.Runtime;

namespace Orleans.AdvancedReminders;

/// <summary>
/// Convenience overloads for interval registration APIs built on top of <see cref="ReminderSchedule"/>.
/// </summary>
public static class ReminderRegistrationExtensions
{
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderRegistry registry,
        GrainId callingGrainId,
        string reminderName,
        TimeSpan dueTime,
        TimeSpan period)
        => registry.RegisterOrUpdateReminder(callingGrainId, reminderName, ReminderSchedule.Interval(dueTime, period), Runtime.ReminderPriority.Normal, Runtime.MissedReminderAction.Skip);

    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderRegistry registry,
        GrainId callingGrainId,
        string reminderName,
        DateTime dueAtUtc,
        TimeSpan period)
        => registry.RegisterOrUpdateReminder(callingGrainId, reminderName, ReminderSchedule.Interval(dueAtUtc, period), Runtime.ReminderPriority.Normal, Runtime.MissedReminderAction.Skip);

    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderRegistry registry,
        GrainId callingGrainId,
        string reminderName,
        TimeSpan dueTime,
        TimeSpan period,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => registry.RegisterOrUpdateReminder(callingGrainId, reminderName, ReminderSchedule.Interval(dueTime, period), priority, action);

    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderRegistry registry,
        GrainId callingGrainId,
        string reminderName,
        DateTime dueAtUtc,
        TimeSpan period,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => registry.RegisterOrUpdateReminder(callingGrainId, reminderName, ReminderSchedule.Interval(dueAtUtc, period), priority, action);

    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderService service,
        GrainId grainId,
        string reminderName,
        TimeSpan dueTime,
        TimeSpan period)
        => service.RegisterOrUpdateReminder(grainId, reminderName, ReminderSchedule.Interval(dueTime, period), Runtime.ReminderPriority.Normal, Runtime.MissedReminderAction.Skip);

    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderService service,
        GrainId grainId,
        string reminderName,
        DateTime dueAtUtc,
        TimeSpan period)
        => service.RegisterOrUpdateReminder(grainId, reminderName, ReminderSchedule.Interval(dueAtUtc, period), Runtime.ReminderPriority.Normal, Runtime.MissedReminderAction.Skip);

    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderService service,
        GrainId grainId,
        string reminderName,
        TimeSpan dueTime,
        TimeSpan period,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => service.RegisterOrUpdateReminder(grainId, reminderName, ReminderSchedule.Interval(dueTime, period), priority, action);

    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderService service,
        GrainId grainId,
        string reminderName,
        DateTime dueAtUtc,
        TimeSpan period,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => service.RegisterOrUpdateReminder(grainId, reminderName, ReminderSchedule.Interval(dueAtUtc, period), priority, action);
}
