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
    /// Wait indefinitely for a permit to become available. Unless an earlier composed gate
    /// established a shared <see cref="WaitUpTo(TimeSpan)"/> deadline, the acquire only completes
    /// when either a permit is granted or the supplied cancellation token is cancelled. This is
    /// the safest mode for not losing ticks when all composed gates use <see cref="Wait"/>, at the
    /// cost of unbounded tardiness.
    /// </summary>
    public static ThrottleBlockMode Wait { get; } = new WaitForever();

    /// <summary>
    /// Wait up to <paramref name="timeout"/> for a permit. If no permit becomes available
    /// in time, the acquire returns <see cref="ReminderAdmissionOutcome.Skipped"/> with
    /// reason <see cref="ReminderSkipReason.AcquireTimeout"/>.
    /// </summary>
    /// <param name="timeout">The maximum time to wait. Must be greater than <see cref="TimeSpan.Zero"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is non-positive.</exception>
    public static ThrottleBlockMode WaitUpTo(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be greater than zero. Use ThrottleBlockMode.SkipImmediately to skip when no permit is available, or ThrottleBlockMode.Wait to wait indefinitely.");
        }

        return new WaitWithTimeout(timeout);
    }

    /// <summary>
    /// Return <see cref="ReminderAdmissionOutcome.Skipped"/> immediately if no permit is
    /// available. Maximum downstream protection; ticks are dropped when the limit is hit.
    /// </summary>
    public static ThrottleBlockMode SkipImmediately { get; } = new SkipImmediatelyMode();

    /// <summary>Concrete representation of <see cref="Wait"/>.</summary>
    internal sealed record WaitForever : ThrottleBlockMode;

    /// <summary>Concrete representation of <see cref="WaitUpTo"/>.</summary>
    internal sealed record WaitWithTimeout(TimeSpan Timeout) : ThrottleBlockMode;

    /// <summary>Concrete representation of <see cref="SkipImmediately"/>.</summary>
    internal sealed record SkipImmediatelyMode : ThrottleBlockMode;
}
