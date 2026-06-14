namespace Orleans.Reminders.Concurrency;

/// <summary>
/// Describes how a cluster-wide <see cref="IReminderDeliveryThrottle"/> behaves when the
/// coordinator that issues permits is temporarily unreachable. This option only has an
/// effect for cluster-scoped tiers (introduced in a later phase); local tiers are always
/// in-process and never observe coordinator outages.
/// </summary>
/// <remarks>
/// This type is part of the public SPI in preparation for the cluster-tier work and is
/// referenced by validators today. Choose <see cref="Open"/> if downstream protection is
/// best-effort; choose <see cref="Closed"/> if exceeding the configured limit is worse
/// than dropping ticks.
/// </remarks>
public enum ThrottleFailureMode
{
    /// <summary>
    /// Deliver the tick if the coordinator is unreachable. Protection lapses for the
    /// duration of the outage, but tick delivery continues. Use when availability is
    /// more important than strict rate limiting.
    /// </summary>
    Open,

    /// <summary>
    /// Skip the tick (<see cref="ReminderSkipReason.CoordinatorUnreachableFailClosed"/>)
    /// if the coordinator is unreachable. Rate limiting is honored at the cost of dropped
    /// ticks during the outage. Use when exceeding the configured limit is worse than
    /// dropping ticks.
    /// </summary>
    Closed,
}
