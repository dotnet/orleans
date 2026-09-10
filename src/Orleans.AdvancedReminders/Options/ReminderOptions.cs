namespace Orleans.AdvancedReminders;

/// <summary>
/// Options for the reminder service.
/// </summary>
public sealed class ReminderOptions
{
    /// <summary>
    /// Gets or sets the minimum period for reminders.
    /// </summary>
    /// <remarks>
    /// High-frequency reminders are dangerous for production systems.
    /// </remarks>
    public TimeSpan MinimumReminderPeriod { get; set; } = TimeSpan.FromMinutes(ReminderOptionsDefaults.MinimumReminderPeriodMinutes);

    /// <summary>
    /// Gets or sets the maximum amount of time to attempt to initialize reminders before giving up.
    /// </summary>
    /// <value>Attempt to initialize for 5 minutes before giving up by default.</value>
    public TimeSpan InitializationTimeout { get; set; } = TimeSpan.FromMinutes(ReminderOptionsDefaults.InitializationTimeoutMinutes);

    /// <summary>
    /// Gets or sets the grace period after a scheduled fire time before a reminder is considered missed.
    /// </summary>
    public TimeSpan MissedReminderGracePeriod { get; set; } = TimeSpan.FromSeconds(ReminderOptionsDefaults.MissedReminderGracePeriodSeconds);

    /// <summary>
    /// Gets or sets whether a due reminder is deleted when no active silo declares its target grain type.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="false"/>. Enable this only when grain types are retired deliberately,
    /// since a type can be temporarily unavailable during deployment or cluster recovery.
    /// </remarks>
    public bool DeleteReminderWhenGrainTypeIsUnavailable { get; set; } = false;

    /// <summary>
    /// Gets or sets the delivery retry policy.
    /// </summary>
    /// <remarks>
    /// The default is <c>ReminderRetryPolicy.Limited(3)</c>, including the initial delivery.
    /// Limited policies retry the same Durable Job occurrence and remove the registration at the limit.
    /// The Durable Jobs retry policy must permit at least that many attempts.
    /// <see cref="ReminderRetryPolicy.NoRetries"/> advances a failed recurring occurrence or removes a one-shot.
    /// <see cref="ReminderRetryPolicy.UntilSuccess"/> retains failures, including those before callback entry,
    /// and schedules a fresh delivery after <see cref="MinimumReminderPeriod"/>. Recurring schedules retain
    /// their original occurrence and calendar anchor until success. Successful one-shots are removed.
    /// Application effects must remain idempotent because delivery can be uncertain.
    /// </remarks>
    public ReminderRetryPolicy DeliveryRetryPolicy { get; set; } = ReminderRetryPolicy.Limited(ReminderOptionsDefaults.MaximumDeliveryAttempts);
}
