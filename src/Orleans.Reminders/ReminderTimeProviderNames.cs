namespace Orleans.Reminders;

/// <summary>
/// Service key used to resolve the reminder subsystem's <see cref="System.TimeProvider"/> from keyed dependency
/// injection. See <see cref="Orleans.Runtime.TimeProviderNames"/> for the core runtime areas.
/// </summary>
public static class ReminderTimeProviderNames
{
    /// <summary>
    /// The clock used by the reminder subsystem.
    /// </summary>
    public const string Reminders = "Orleans.Reminders";
}
