#nullable enable
using Orleans.Runtime;

namespace Orleans;

/// <summary>
/// Registers reminders for a grain type on activation if missing.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class RegisterReminderAttribute : Attribute
{
    /// <summary>
    /// Initializes a new interval-based reminder attribute.
    /// </summary>
    public RegisterReminderAttribute(
        string name,
        double dueSeconds,
        double periodSeconds,
        Runtime.ReminderPriority priority = Runtime.ReminderPriority.Normal,
        Runtime.MissedReminderAction action = Runtime.MissedReminderAction.Skip)
    {
        ValidateName(name);
        ValidatePriorityAndAction(priority, action);
        ValidateNonNegativeFinite(dueSeconds, nameof(dueSeconds));
        ValidatePositiveFinite(periodSeconds, nameof(periodSeconds));

        Name = name;
        Due = TimeSpan.FromSeconds(dueSeconds);
        Period = TimeSpan.FromSeconds(periodSeconds);
        Priority = priority;
        Action = action;
    }

    /// <summary>
    /// Initializes a new cron-based reminder attribute.
    /// </summary>
    public RegisterReminderAttribute(
        string name,
        string cron,
        Runtime.ReminderPriority priority = Runtime.ReminderPriority.Normal,
        Runtime.MissedReminderAction action = Runtime.MissedReminderAction.Skip)
    {
        ValidateName(name);
        ValidatePriorityAndAction(priority, action);
        ValidateCron(cron);

        Name = name;
        Cron = cron;
        Priority = priority;
        Action = action;
    }

    /// <summary>
    /// Gets the reminder name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the interval due time.
    /// </summary>
    public TimeSpan? Due { get; }

    /// <summary>
    /// Gets the interval period.
    /// </summary>
    public TimeSpan? Period { get; }

    /// <summary>
    /// Gets the cron expression.
    /// </summary>
    public string? Cron { get; }

    /// <summary>
    /// Gets the reminder priority.
    /// </summary>
    public Runtime.ReminderPriority Priority { get; }

    /// <summary>
    /// Gets the missed reminder action.
    /// </summary>
    public Runtime.MissedReminderAction Action { get; }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Reminder name must be non-empty.", nameof(name));
        }
    }

    private static void ValidateCron(string cron)
    {
        if (string.IsNullOrWhiteSpace(cron))
        {
            throw new ArgumentException("Cron expression must be non-empty.", nameof(cron));
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

    private static void ValidateNonNegativeFinite(double value, string argumentName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(argumentName);
        }
    }

    private static void ValidatePositiveFinite(double value, string argumentName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(argumentName);
        }
    }
}
