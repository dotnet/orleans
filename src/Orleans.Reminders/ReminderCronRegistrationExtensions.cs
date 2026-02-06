#nullable enable
using System;
using System.Threading.Tasks;
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
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(cronExpression);
        return registry.RegisterOrUpdateReminder(callingGrainId, reminderName, cronExpression.ToExpressionString());
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
        return registry.RegisterOrUpdateReminder(callingGrainId, reminderName, cronBuilder.ToExpressionString());
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
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(cronExpression);
        return registry.RegisterOrUpdateReminder(callingGrainId, reminderName, cronExpression.ToExpressionString(), priority, action);
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
        return registry.RegisterOrUpdateReminder(callingGrainId, reminderName, cronBuilder.ToExpressionString(), priority, action);
    }

    /// <summary>
    /// Registers or updates a cron reminder via <see cref="IReminderService"/> using a typed cron expression.
    /// </summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderService service,
        GrainId grainId,
        string reminderName,
        ReminderCronExpression cronExpression)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(cronExpression);
        return service.RegisterOrUpdateReminder(grainId, reminderName, cronExpression.ToExpressionString());
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
        return service.RegisterOrUpdateReminder(grainId, reminderName, cronBuilder.ToExpressionString());
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
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(cronExpression);
        return service.RegisterOrUpdateReminder(grainId, reminderName, cronExpression.ToExpressionString(), priority, action);
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
        return service.RegisterOrUpdateReminder(grainId, reminderName, cronBuilder.ToExpressionString(), priority, action);
    }
}
