using System;
using System.Threading;

namespace Orleans.Reminders.Concurrency;

/// <summary>
/// A lease returned by <see cref="IReminderDeliveryThrottle.AcquireAsync"/>. Callers must
/// dispose the lease exactly once after the dispatch attempt completes (or is abandoned),
/// regardless of dispatch success or failure. A lease whose outcome is
/// <see cref="ReminderAdmissionOutcome.Skipped"/> may be disposed but does so as a no-op.
/// </summary>
/// <remarks>
/// Returning an admitted lease is the admission commit point. Reversible capacity reserved while
/// composing gates is restored if cancellation, a schedule update, a gate rejection, or the shared
/// deadline wins before this point. Lease-scoped capacity is released by <see cref="Dispose"/>.
/// </remarks>
public abstract class ReminderDeliveryLease : IDisposable
{
    /// <summary>
    /// A cached, shared admitted lease that performs no work on dispose. Used by throttles
    /// that do not need to track per-acquire state, such as <see cref="NoOpReminderDeliveryThrottle"/>.
    /// </summary>
    public static ReminderDeliveryLease NoOpAdmitted { get; } = new AdmittedLease(tierName: null, waitedFor: TimeSpan.Zero, releaseAction: null);

    /// <summary>The outcome of the acquire attempt.</summary>
    public abstract ReminderAdmissionOutcome Outcome { get; }

    /// <summary>
    /// The wall-clock duration the caller spent waiting inside <see cref="IReminderDeliveryThrottle.AcquireAsync"/>.
    /// Zero for immediate admit or immediate skip.
    /// </summary>
    public abstract TimeSpan WaitedFor { get; }

    /// <summary>
    /// The classified skip reason. Non-null when <see cref="Outcome"/> is
    /// <see cref="ReminderAdmissionOutcome.Skipped"/>; null when admitted.
    /// </summary>
    public abstract ReminderSkipReason? SkipReason { get; }

    /// <summary>
    /// The name of the tier that produced this outcome. For admits, this is the most-restrictive
    /// tier that admitted; for skips, this is the tier that denied. May be null when no specific
    /// tier attribution applies (e.g., the default no-op throttle).
    /// </summary>
    public abstract string? TierName { get; }

    /// <summary>Releases any state associated with this lease.</summary>
    public abstract void Dispose();

    /// <summary>Builds an admitted lease.</summary>
    /// <param name="tierName">The name of the tier that admitted the acquire.</param>
    /// <param name="waitedFor">The time the caller spent waiting for admission.</param>
    /// <param name="releaseAction">Optional action invoked on the first <see cref="Dispose"/> call.</param>
    public static ReminderDeliveryLease Admitted(string? tierName, TimeSpan waitedFor, Action? releaseAction)
        => new AdmittedLease(tierName, waitedFor, releaseAction);

    /// <summary>Builds a skipped lease.</summary>
    /// <param name="tierName">The name of the tier that produced the skip.</param>
    /// <param name="waitedFor">The time the caller spent waiting before being skipped.</param>
    /// <param name="reason">The classified skip reason.</param>
    public static ReminderDeliveryLease Skipped(string? tierName, TimeSpan waitedFor, ReminderSkipReason reason)
        => new SkippedLease(tierName, waitedFor, reason);

    private sealed class AdmittedLease : ReminderDeliveryLease
    {
        private Action? _releaseAction;

        public AdmittedLease(string? tierName, TimeSpan waitedFor, Action? releaseAction)
        {
            TierName = tierName;
            WaitedFor = waitedFor;
            _releaseAction = releaseAction;
        }

        public override ReminderAdmissionOutcome Outcome => ReminderAdmissionOutcome.Admitted;
        public override TimeSpan WaitedFor { get; }
        public override ReminderSkipReason? SkipReason => null;
        public override string? TierName { get; }

        public override void Dispose()
        {
            var action = Interlocked.Exchange(ref _releaseAction, null);
            action?.Invoke();
        }
    }

    private sealed class SkippedLease : ReminderDeliveryLease
    {
        public SkippedLease(string? tierName, TimeSpan waitedFor, ReminderSkipReason reason)
        {
            TierName = tierName;
            WaitedFor = waitedFor;
            SkipReason = reason;
        }

        public override ReminderAdmissionOutcome Outcome => ReminderAdmissionOutcome.Skipped;
        public override TimeSpan WaitedFor { get; }
        public override ReminderSkipReason? SkipReason { get; }
        public override string? TierName { get; }

        public override void Dispose()
        {
            // Skipped leases have no state to release.
        }
    }
}
