namespace Orleans.Reminders.Concurrency;

/// <summary>
/// The outcome of attempting to acquire a delivery lease from an <see cref="IReminderDeliveryThrottle"/>.
/// </summary>
public enum ReminderAdmissionOutcome
{
    /// <summary>
    /// The acquire request was admitted. The caller holds a lease and may dispatch the reminder tick.
    /// The caller is responsible for disposing the lease after dispatch (regardless of dispatch success).
    /// </summary>
    Admitted,

    /// <summary>
    /// The acquire request was skipped by the throttle. The caller must not dispatch the reminder tick.
    /// The grain will not observe this tick; the next periodic tick will be considered independently.
    /// </summary>
    Skipped,
}
