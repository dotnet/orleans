namespace Orleans.Reminders.Concurrency;

/// <summary>
/// Classifies the reason an <see cref="IReminderDeliveryThrottle"/> skipped a reminder tick.
/// Used as the value of the <c>orleans.reminder.throttle.skip_reason</c> attribute on
/// diagnostic events, metrics, and activity tags.
/// </summary>
public enum ReminderSkipReason
{
    /// <summary>
    /// A local (in-process) limiter rejected the acquire because its capacity was exhausted
    /// and the configured block mode was <see cref="ThrottleBlockMode.SkipImmediately"/>.
    /// </summary>
    LocalLimiterFull,

    /// <summary>
    /// A cluster-wide limiter rejected the acquire because cluster-wide capacity was exhausted
    /// and the configured block mode was <see cref="ThrottleBlockMode.SkipImmediately"/>.
    /// </summary>
    /// <remarks>Reserved for cluster tiers introduced in later phases.</remarks>
    ClusterLimiterFull,

    /// <summary>
    /// The acquire waited up to the configured maximum and a permit did not become available
    /// in time. The configured block mode was <see cref="ThrottleBlockMode.WaitUpTo"/>.
    /// </summary>
    AcquireTimeout,

    /// <summary>
    /// The cluster-wide coordinator that issues permits was unreachable and the configured
    /// failure mode skips ticks (fail-closed). Reserved for cluster tiers introduced in later phases.
    /// </summary>
    CoordinatorUnreachableFailClosed,

    /// <summary>
    /// The silo's <see cref="Orleans.Runtime.Messaging.IOverloadDetector"/> reported overload (CPU/memory
    /// pressure exceeding the configured load-shedding thresholds) and the configured behavior
    /// did not admit the tick before the overload cleared.
    /// </summary>
    SiloOverloaded,

    /// <summary>
    /// The throttle's slow-start ramp-up has not yet reached the capacity required to admit this
    /// tick. The acquire either timed out waiting for capacity (<see cref="ThrottleBlockMode.WaitUpTo"/>)
    /// or was rejected immediately (<see cref="ThrottleBlockMode.SkipImmediately"/>).
    /// </summary>
    SlowStartLimited,

    /// <summary>
    /// The silo is shutting down. The reminder loop is unwinding and no further ticks will be
    /// dispatched until the reminder is reactivated.
    /// </summary>
    SiloShutdown,
}
