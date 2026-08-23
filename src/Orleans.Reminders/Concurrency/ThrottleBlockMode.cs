using System;

namespace Orleans.Reminders.Concurrency;

/// <summary>
/// Describes how an <see cref="IReminderDeliveryThrottle"/> behaves when an acquire cannot
/// be admitted immediately. Constructed via the static factory members on this type to
/// prevent inconsistent configurations (for example, a "wait with timeout" choice that
/// is missing a timeout value).
/// </summary>
public abstract record ThrottleBlockMode
{
    private protected ThrottleBlockMode()
    {
    }

    /// <summary>
    /// Wait for a permit to become available. If any composed gate uses
    /// <see cref="WaitUpTo(TimeSpan)"/>, the shortest configured timeout establishes one deadline
    /// for the complete acquire, including gates configured with <see cref="Wait"/>. When every
    /// gate uses <see cref="Wait"/>, the acquire only completes when a permit is granted or the
    /// supplied cancellation token is cancelled, at the cost of unbounded tardiness.
    /// </summary>
    public static ThrottleBlockMode Wait { get; } = new WaitForever();

    /// <summary>
    /// Contributes <paramref name="timeout"/> to the composed acquire's absolute deadline. The
    /// shortest configured timeout is measured from the beginning of the acquire and bounds every
    /// gate and the final admission commit. If admission does not commit in time, the acquire
    /// returns <see cref="ReminderAdmissionOutcome.Skipped"/> with a reason such as
    /// <see cref="ReminderSkipReason.AcquireTimeout"/>,
    /// <see cref="ReminderSkipReason.SiloOverloaded"/>, or
    /// <see cref="ReminderSkipReason.SlowStartLimited"/>.
    /// </summary>
    /// <param name="timeout">The maximum time to wait. Must be greater than <see cref="TimeSpan.Zero"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The timeout is non-positive or exceeds the runtime timer limit of 4294967294 milliseconds.
    /// </exception>
    public static ThrottleBlockMode WaitUpTo(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be greater than zero. Use ThrottleBlockMode.SkipImmediately to skip when no permit is available, or ThrottleBlockMode.Wait to wait indefinitely.");
        }

        if (timeout > ReminderThrottleTime.MaxTimerDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, $"The timeout must be less than or equal to {ReminderThrottleTime.MaxTimerDelay}.");
        }

        return new WaitWithTimeout(timeout);
    }

    /// <summary>
    /// Return <see cref="ReminderAdmissionOutcome.Skipped"/> immediately if no permit is
    /// available. Maximum downstream protection; ticks are dropped when the limit is hit.
    /// </summary>
    public static ThrottleBlockMode SkipImmediately { get; } = new SkipImmediatelyMode();

    /// <summary>Concrete representation of <see cref="Wait"/>.</summary>
    internal sealed record WaitForever : ThrottleBlockMode
    {
        public override string ToString() => nameof(Wait);
    }

    /// <summary>Concrete representation of <see cref="WaitUpTo"/>.</summary>
    internal sealed record WaitWithTimeout(TimeSpan Timeout) : ThrottleBlockMode
    {
        public override string ToString() => $"{nameof(WaitUpTo)}({Timeout})";
    }

    /// <summary>Concrete representation of <see cref="SkipImmediately"/>.</summary>
    internal sealed record SkipImmediatelyMode : ThrottleBlockMode
    {
        public override string ToString() => nameof(SkipImmediately);
    }
}

internal static class ReminderThrottleTime
{
    public static readonly TimeSpan MaxTimerDelay = TimeSpan.FromMilliseconds(0xfffffffe);
}
