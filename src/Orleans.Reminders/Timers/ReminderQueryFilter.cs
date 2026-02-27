using System;

#nullable enable
namespace Orleans;

/// <summary>
/// Status categories used by <see cref="ReminderQueryFilter"/> for due-state filtering.
/// </summary>
[Flags]
public enum ReminderQueryStatus : byte
{
    /// <summary>
    /// No status filtering.
    /// </summary>
    Any = 0,

    /// <summary>
    /// Matches reminders whose due time is less than or equal to now.
    /// </summary>
    Due = 1 << 0,

    /// <summary>
    /// Matches reminders overdue by at least <see cref="ReminderQueryFilter.OverdueBy"/>.
    /// </summary>
    Overdue = 1 << 1,

    /// <summary>
    /// Matches reminders considered missed: due time older than <see cref="ReminderQueryFilter.MissedBy"/>
    /// and a last-fire timestamp earlier than that due time (or missing).
    /// </summary>
    Missed = 1 << 2,

    /// <summary>
    /// Matches reminders due strictly after now.
    /// </summary>
    Upcoming = 1 << 3,
}

/// <summary>
/// Server-side filter options for reminder management paging queries.
/// </summary>
[GenerateSerializer]
public sealed class ReminderQueryFilter
{
    /// <summary>
    /// Gets the inclusive due lower bound in UTC. Null means no lower bound.
    /// </summary>
    [Id(0)]
    public DateTime? DueFromUtcInclusive { get; init; }

    /// <summary>
    /// Gets the inclusive due upper bound in UTC. Null means no upper bound.
    /// </summary>
    [Id(1)]
    public DateTime? DueToUtcInclusive { get; init; }

    /// <summary>
    /// Gets the optional reminder priority to match.
    /// </summary>
    [Id(2)]
    public Runtime.ReminderPriority? Priority { get; init; }

    /// <summary>
    /// Gets the optional missed-reminder action to match.
    /// </summary>
    [Id(3)]
    public Runtime.MissedReminderAction? Action { get; init; }

    /// <summary>
    /// Gets the optional schedule kind to match.
    /// </summary>
    [Id(4)]
    public Runtime.ReminderScheduleKind? ScheduleKind { get; init; }

    /// <summary>
    /// Gets the due-state status filter mask.
    /// </summary>
    [Id(5)]
    public ReminderQueryStatus Status { get; init; } = ReminderQueryStatus.Any;

    /// <summary>
    /// Gets the overdue threshold used when <see cref="ReminderQueryStatus.Overdue"/> is set.
    /// </summary>
    [Id(6)]
    public TimeSpan OverdueBy { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Gets the missed threshold used when <see cref="ReminderQueryStatus.Missed"/> is set.
    /// </summary>
    [Id(7)]
    public TimeSpan MissedBy { get; init; } = TimeSpan.Zero;
}
