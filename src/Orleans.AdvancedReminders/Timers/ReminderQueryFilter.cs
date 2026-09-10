#nullable enable
namespace Orleans.AdvancedReminders;

/// <summary>
/// Server-side filter options for reminder management paging queries.
/// </summary>
[GenerateSerializer]
public sealed class ReminderQueryFilter
{
    /// <summary>
    /// Gets the optional missed-reminder action to match.
    /// </summary>
    [Id(0)]
    public Runtime.MissedReminderAction? Action { get; init; }
    /// <summary>
    /// Gets the inclusive due lower bound in UTC. Null means no lower bound.
    /// </summary>
    [Id(1)]
    public DateTime? DueFromUtcInclusive { get; init; }

    /// <summary>
    /// Gets the inclusive due upper bound in UTC. Null means no upper bound.
    /// </summary>
    [Id(2)]
    public DateTime? DueToUtcInclusive { get; init; }

    /// <summary>
    /// Gets the optional target grain type to match.
    /// </summary>
    [Id(3)]
    public GrainType? GrainType { get; init; }

    /// <summary>
    /// Gets the missed threshold used when <see cref="ReminderQueryStatus.Missed"/> is set.
    /// </summary>
    [Id(4)]
    public TimeSpan MissedBy { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Gets the overdue threshold used when <see cref="ReminderQueryStatus.Overdue"/> is set.
    /// </summary>
    [Id(5)]
    public TimeSpan OverdueBy { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Gets the optional schedule kind to match.
    /// </summary>
    [Id(6)]
    public Runtime.ReminderScheduleKind? ScheduleKind { get; init; }

    /// <summary>
    /// Gets the due-state status filter mask.
    /// </summary>
    [Id(7)]
    public ReminderQueryStatus Status { get; init; } = ReminderQueryStatus.Any;
}
