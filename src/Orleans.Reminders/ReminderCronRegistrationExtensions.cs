#nullable enable
using System;
using System.Threading.Tasks;
using Orleans.Reminders.Cron.Internal;
using Orleans.Runtime;
using Orleans.Timers;

namespace Orleans;

/// <summary>
/// Convenience overloads for cron registration APIs using typed cron objects.
/// </summary>
public static class ReminderCronRegistrationExtensions
{
    /// <summary>
    /// Registers or updates a cron reminder via <see cref="IReminderRegistry"/> using a typed cron expression.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderRegistry registry,
        GrainId callingGrainId,
        string reminderName,
        ReminderCronExpression cronExpression)
        => RegisterOrUpdateReminder(registry, callingGrainId, reminderName, cronExpression, timeZone: null);

    /// <summary>
    /// Registers or updates a cron reminder via <see cref="IReminderRegistry"/> using a typed cron expression.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderRegistry registry,
        GrainId callingGrainId,
        string reminderName,
        ReminderCronExpression cronExpression,
        TimeZoneInfo? timeZone)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(cronExpression);
        return registry.RegisterOrUpdateReminder(
            callingGrainId,
            reminderName,
            cronExpression.ToExpressionString(),
            NormalizeTimeZoneId(timeZone));
    }

    /// <summary>
    /// Registers or updates a cron reminder via <see cref="IReminderRegistry"/> using a cron builder.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderRegistry registry,
        GrainId callingGrainId,
        string reminderName,
        ReminderCronBuilder cronBuilder)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(cronBuilder);
        return RegisterOrUpdateReminder(
            registry,
            callingGrainId,
            reminderName,
            cronBuilder.ToExpressionString(),
            cronBuilder.TimeZone);
    }

    /// <summary>
    /// Registers or updates a cron reminder via <see cref="IReminderRegistry"/> using an expression and time zone.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderRegistry registry,
        GrainId callingGrainId,
        string reminderName,
        string cronExpression,
        TimeZoneInfo? timeZone)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.RegisterOrUpdateReminder(
            callingGrainId,
            reminderName,
            cronExpression,
            NormalizeTimeZoneId(timeZone));
    }

    /// <summary>
    /// Registers or updates a cron reminder via <see cref="IReminderRegistry"/> using a typed cron expression.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderRegistry registry,
        GrainId callingGrainId,
        string reminderName,
        ReminderCronExpression cronExpression,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateReminder(registry, callingGrainId, reminderName, cronExpression, priority, action, timeZone: null);

    /// <summary>
    /// Registers or updates a cron reminder via <see cref="IReminderRegistry"/> using a typed cron expression.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderRegistry registry,
        GrainId callingGrainId,
        string reminderName,
        ReminderCronExpression cronExpression,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action,
        TimeZoneInfo? timeZone)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(cronExpression);
        return registry.RegisterOrUpdateReminder(
            callingGrainId,
            reminderName,
            cronExpression.ToExpressionString(),
            priority,
            action,
            NormalizeTimeZoneId(timeZone));
    }

    /// <summary>
    /// Registers or updates a cron reminder via <see cref="IReminderRegistry"/> using a cron builder.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderRegistry registry,
        GrainId callingGrainId,
        string reminderName,
        ReminderCronBuilder cronBuilder,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(cronBuilder);
        return RegisterOrUpdateReminder(
            registry,
            callingGrainId,
            reminderName,
            cronBuilder.ToExpressionString(),
            cronBuilder.TimeZone,
            priority,
            action);
    }

    /// <summary>
    /// Registers or updates a cron reminder via <see cref="IReminderRegistry"/> using an expression and time zone.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderRegistry registry,
        GrainId callingGrainId,
        string reminderName,
        string cronExpression,
        TimeZoneInfo? timeZone,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.RegisterOrUpdateReminder(
            callingGrainId,
            reminderName,
            cronExpression,
            priority,
            action,
            NormalizeTimeZoneId(timeZone));
    }

    /// <summary>
    /// Registers or updates a cron reminder via <see cref="IReminderService"/> using a typed cron expression.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderService service,
        GrainId grainId,
        string reminderName,
        ReminderCronExpression cronExpression)
        => RegisterOrUpdateReminder(service, grainId, reminderName, cronExpression, timeZone: null);

    /// <summary>
    /// Registers or updates a cron reminder via <see cref="IReminderService"/> using a typed cron expression.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderService service,
        GrainId grainId,
        string reminderName,
        ReminderCronExpression cronExpression,
        TimeZoneInfo? timeZone)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(cronExpression);
        return service.RegisterOrUpdateReminder(
            grainId,
            reminderName,
            cronExpression.ToExpressionString(),
            NormalizeTimeZoneId(timeZone));
    }

    /// <summary>
    /// Registers or updates a cron reminder via <see cref="IReminderService"/> using a cron builder.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderService service,
        GrainId grainId,
        string reminderName,
        ReminderCronBuilder cronBuilder)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(cronBuilder);
        return RegisterOrUpdateReminder(
            service,
            grainId,
            reminderName,
            cronBuilder.ToExpressionString(),
            cronBuilder.TimeZone);
    }

    /// <summary>
    /// Registers or updates a cron reminder via <see cref="IReminderService"/> using an expression and time zone.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderService service,
        GrainId grainId,
        string reminderName,
        string cronExpression,
        TimeZoneInfo? timeZone)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service.RegisterOrUpdateReminder(
            grainId,
            reminderName,
            cronExpression,
            NormalizeTimeZoneId(timeZone));
    }

    /// <summary>
    /// Registers or updates a cron reminder via <see cref="IReminderService"/> using a typed cron expression.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderService service,
        GrainId grainId,
        string reminderName,
        ReminderCronExpression cronExpression,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateReminder(service, grainId, reminderName, cronExpression, priority, action, timeZone: null);

    /// <summary>
    /// Registers or updates a cron reminder via <see cref="IReminderService"/> using a typed cron expression.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderService service,
        GrainId grainId,
        string reminderName,
        ReminderCronExpression cronExpression,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action,
        TimeZoneInfo? timeZone)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(cronExpression);
        return service.RegisterOrUpdateReminder(
            grainId,
            reminderName,
            cronExpression.ToExpressionString(),
            priority,
            action,
            NormalizeTimeZoneId(timeZone));
    }

    /// <summary>
    /// Registers or updates a cron reminder via <see cref="IReminderService"/> using a cron builder.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderService service,
        GrainId grainId,
        string reminderName,
        ReminderCronBuilder cronBuilder,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(cronBuilder);
        return RegisterOrUpdateReminder(
            service,
            grainId,
            reminderName,
            cronBuilder.ToExpressionString(),
            cronBuilder.TimeZone,
            priority,
            action);
    }

    /// <summary>
    /// Registers or updates a cron reminder via <see cref="IReminderService"/> using an expression and time zone.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderService service,
        GrainId grainId,
        string reminderName,
        string cronExpression,
        TimeZoneInfo? timeZone,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service.RegisterOrUpdateReminder(
            grainId,
            reminderName,
            cronExpression,
            priority,
            action,
            NormalizeTimeZoneId(timeZone));
    }

    private static string? NormalizeTimeZoneId(TimeZoneInfo? timeZone)
        => ReminderCronSchedule.NormalizeTimeZoneIdForStorage(timeZone);
}
