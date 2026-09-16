#nullable enable
using Orleans.AdvancedReminders.Cron.Internal;
using Orleans.AdvancedReminders.Timers;
using Orleans.Runtime;

namespace Orleans.AdvancedReminders;

/// <summary>
/// Convenience overloads for cron registration APIs using typed cron objects.
/// </summary>
public static class ReminderCronRegistrationExtensions
{
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderRegistry registry,
        GrainId callingGrainId,
        string reminderName,
        string cronExpression)
        => RegisterCronReminder(registry, callingGrainId, reminderName, cronExpression, cronTimeZoneId: null, Runtime.MissedReminderAction.Skip);

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
        return RegisterCronReminder(
            registry,
            callingGrainId,
            reminderName,
            cronExpression.ToExpressionString(),
            ReminderCronSchedule.NormalizeTimeZoneIdForStorage(timeZone),
            Runtime.MissedReminderAction.Skip);
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
        return RegisterCronReminder(
            registry,
            callingGrainId,
            reminderName,
            cronExpression,
            ReminderCronSchedule.NormalizeTimeZoneIdForStorage(timeZone),
            Runtime.MissedReminderAction.Skip);
    }

    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderRegistry registry,
        GrainId callingGrainId,
        string reminderName,
        string cronExpression,
        Runtime.MissedReminderAction action)
        => RegisterCronReminder(registry, callingGrainId, reminderName, cronExpression, cronTimeZoneId: null, action);

    /// <summary>
    /// Registers or updates a cron reminder via <see cref="IReminderRegistry"/> using a typed cron expression.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderRegistry registry,
        GrainId callingGrainId,
        string reminderName,
        ReminderCronExpression cronExpression,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateReminder(registry, callingGrainId, reminderName, cronExpression, action, timeZone: null);

    /// <summary>
    /// Registers or updates a cron reminder via <see cref="IReminderRegistry"/> using a typed cron expression.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderRegistry registry,
        GrainId callingGrainId,
        string reminderName,
        ReminderCronExpression cronExpression,
        Runtime.MissedReminderAction action,
        TimeZoneInfo? timeZone)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(cronExpression);
        return registry.RegisterOrUpdateReminder(
            callingGrainId,
            reminderName,
            cronExpression.ToExpressionString(),
            action,
            ReminderCronSchedule.NormalizeTimeZoneIdForStorage(timeZone));
    }

    /// <summary>
    /// Registers or updates a cron reminder via <see cref="IReminderRegistry"/> using a cron builder.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderRegistry registry,
        GrainId callingGrainId,
        string reminderName,
        ReminderCronBuilder cronBuilder,
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
        Runtime.MissedReminderAction action)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return RegisterCronReminder(
            registry,
            callingGrainId,
            reminderName,
            cronExpression,
            ReminderCronSchedule.NormalizeTimeZoneIdForStorage(timeZone),
            action);
    }

    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderRegistry registry,
        GrainId callingGrainId,
        string reminderName,
        string cronExpression,
        Runtime.MissedReminderAction action,
        string? cronTimeZoneId)
        => RegisterCronReminder(registry, callingGrainId, reminderName, cronExpression, cronTimeZoneId, action);

    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderService service,
        GrainId grainId,
        string reminderName,
        string cronExpression)
        => RegisterCronReminder(service, grainId, reminderName, cronExpression, cronTimeZoneId: null, Runtime.MissedReminderAction.Skip);

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
        return RegisterCronReminder(
            service,
            grainId,
            reminderName,
            cronExpression.ToExpressionString(),
            ReminderCronSchedule.NormalizeTimeZoneIdForStorage(timeZone),
            Runtime.MissedReminderAction.Skip);
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
        return RegisterCronReminder(
            service,
            grainId,
            reminderName,
            cronExpression,
            ReminderCronSchedule.NormalizeTimeZoneIdForStorage(timeZone),
            Runtime.MissedReminderAction.Skip);
    }

    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderService service,
        GrainId grainId,
        string reminderName,
        string cronExpression,
        Runtime.MissedReminderAction action)
        => RegisterCronReminder(service, grainId, reminderName, cronExpression, cronTimeZoneId: null, action);

    /// <summary>
    /// Registers or updates a cron reminder via <see cref="IReminderService"/> using a typed cron expression.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderService service,
        GrainId grainId,
        string reminderName,
        ReminderCronExpression cronExpression,
        Runtime.MissedReminderAction action)
        => RegisterOrUpdateReminder(service, grainId, reminderName, cronExpression, action, timeZone: null);

    /// <summary>
    /// Registers or updates a cron reminder via <see cref="IReminderService"/> using a typed cron expression.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderService service,
        GrainId grainId,
        string reminderName,
        ReminderCronExpression cronExpression,
        Runtime.MissedReminderAction action,
        TimeZoneInfo? timeZone)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(cronExpression);
        return service.RegisterOrUpdateReminder(
            grainId,
            reminderName,
            cronExpression.ToExpressionString(),
            action,
            ReminderCronSchedule.NormalizeTimeZoneIdForStorage(timeZone));
    }

    /// <summary>
    /// Registers or updates a cron reminder via <see cref="IReminderService"/> using a cron builder.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderService service,
        GrainId grainId,
        string reminderName,
        ReminderCronBuilder cronBuilder,
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
        Runtime.MissedReminderAction action)
    {
        ArgumentNullException.ThrowIfNull(service);
        return RegisterCronReminder(
            service,
            grainId,
            reminderName,
            cronExpression,
            ReminderCronSchedule.NormalizeTimeZoneIdForStorage(timeZone),
            action);
    }

    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderService service,
        GrainId grainId,
        string reminderName,
        string cronExpression,
        Runtime.MissedReminderAction action,
        string? cronTimeZoneId)
        => RegisterCronReminder(service, grainId, reminderName, cronExpression, cronTimeZoneId, action);

    private static Task<IGrainReminder> RegisterCronReminder(
        IReminderRegistry registry,
        GrainId callingGrainId,
        string reminderName,
        string cronExpression,
        string? cronTimeZoneId,
        Runtime.MissedReminderAction action)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.RegisterOrUpdateReminder(
            callingGrainId,
            reminderName,
            ReminderSchedule.Cron(cronExpression, cronTimeZoneId),
            action);
    }

    private static Task<IGrainReminder> RegisterCronReminder(
        IReminderService service,
        GrainId grainId,
        string reminderName,
        string cronExpression,
        string? cronTimeZoneId,
        Runtime.MissedReminderAction action)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service.RegisterOrUpdateReminder(
            grainId,
            reminderName,
            ReminderSchedule.Cron(cronExpression, cronTimeZoneId),
            action);
    }
}
