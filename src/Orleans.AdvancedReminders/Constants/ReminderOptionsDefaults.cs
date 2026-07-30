namespace Orleans.AdvancedReminders;

internal static class ReminderOptionsDefaults
{
    /// <summary>
    /// Minimum period for registering a reminder ... we want to enforce a lower bound <see cref="ReminderOptions.MinimumReminderPeriod"/>.
    /// </summary>
    /// <remarks>The default is one minute. Tests can configure a shorter period to reduce their running time.</remarks>
    public const uint MinimumReminderPeriodMinutes = 1;

    /// <summary>
    /// The maximum amount of time (in minutes) to attempt to initialize reminders giving up <see cref="ReminderOptions.InitializationTimeout"/>.
    /// </summary>
    public const uint InitializationTimeoutMinutes = 5;

    /// <summary>
    /// Grace period in seconds before a reminder is considered missed.
    /// </summary>
    public const uint MissedReminderGracePeriodSeconds = 30;
}
