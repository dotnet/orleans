namespace Orleans.AdvancedReminders;

/// <summary>
/// Represents a reminder table entry.
/// </summary>
[Serializable]
[GenerateSerializer]
public sealed class ReminderEntry
{
    /// <summary>
    /// Gets or sets the missed reminder action.
    /// </summary>
    [Id(0)]
    public Runtime.MissedReminderAction Action { get; set; } = Runtime.MissedReminderAction.Skip;

    /// <summary>
    /// Gets or sets the cron expression for this reminder.
    /// If null or empty, the reminder uses <see cref="StartAt"/> and <see cref="Period"/> interval semantics.
    /// </summary>
    [Id(1)]
    public string CronExpression { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the time zone id used to evaluate <see cref="CronExpression"/>.
    /// Null or empty indicates UTC scheduling.
    /// </summary>
    [Id(2)]
    public string CronTimeZoneId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ETag.
    /// </summary>
    /// <value>The ETag.</value>
    [Id(3)]
    public string ETag { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the grain ID of the grain that created the reminder. Forms the reminder
    /// primary key together with <see cref="ReminderName"/>.
    /// </summary>
    [Id(4)]
    public GrainId GrainId { get; set; }

    /// <summary>
    /// Gets or sets the durable job identifier for the currently scheduled occurrence.
    /// </summary>
    [Id(5)]
    public string JobId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the durable job shard identifier for the currently scheduled occurrence.
    /// </summary>
    [Id(6)]
    public string JobShardId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timestamp when this reminder was last fired in UTC.
    /// </summary>
    [Id(7)]
    public DateTime? LastFireUtc { get; set; }

    /// <summary>
    /// Gets or sets the next due timestamp for this reminder in UTC.
    /// </summary>
    [Id(8)]
    public DateTime? NextDueUtc { get; set; }

    /// <summary>
    /// Gets or sets the time period for the reminder
    /// </summary>
    [Id(9)]
    public TimeSpan Period { get; set; }

    /// <summary>
    /// Gets or sets the name of the reminder. Forms the reminder primary key together with
    /// <see cref="GrainId"/>.
    /// </summary>
    [Id(10)]
    public string ReminderName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the stable identifier for the currently scheduled occurrence.
    /// </summary>
    [Id(11)]
    public string ScheduleId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the time when the reminder was supposed to tick in the first time
    /// </summary>
    [Id(12)]
    public DateTime StartAt { get; set; }

    /// <inheritdoc/>
    public override string ToString()
        => $"<GrainId={GrainId} ReminderName={ReminderName} Period={Period} Cron={CronExpression} CronTimeZoneId={CronTimeZoneId} NextDueUtc={NextDueUtc} LastFireUtc={LastFireUtc} Action={Action} ScheduleId={ScheduleId} JobId={JobId} JobShardId={JobShardId}>";

    /// <summary>
    /// Returns an <see cref="IGrainReminder"/> representing the data in this instance.
    /// </summary>
    /// <returns>The <see cref="IGrainReminder"/>.</returns>
    internal IGrainReminder ToIGrainReminder() => new ReminderData(GrainId, ReminderName, ETag, ScheduleId, CronExpression, Action, CronTimeZoneId);
}
