using System.ComponentModel;
using System.Globalization;

namespace Orleans.AdvancedReminders;

/// <summary>
/// Defines how failed advanced reminder deliveries are retried.
/// </summary>
[TypeConverter(typeof(ReminderRetryPolicyConverter))]
public abstract class ReminderRetryPolicy
{
    private ReminderRetryPolicy() { }

    /// <summary>
    /// Gets a policy which logs a failed callback and advances a recurring schedule or removes a one-shot.
    /// </summary>
    public static ReminderRetryPolicy NoRetries { get; } = new NoRetriesPolicy();

    /// <summary>
    /// Gets a policy which retains failed work and schedules delayed deliveries until success or explicit removal.
    /// </summary>
    public static ReminderRetryPolicy UntilSuccess { get; } = new UntilSuccessPolicy();

    /// <summary>
    /// Creates a policy which retries the same occurrence up to the specified Durable Jobs dequeue count.
    /// </summary>
    /// <param name="maximumAttempts">A positive count which includes the initial delivery attempt.</param>
    /// <returns>The limited retry policy.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The attempt count is less than one.</exception>
    public static ReminderRetryPolicy Limited(int maximumAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAttempts, 1);
        return new LimitedPolicy(maximumAttempts);
    }

    internal virtual bool RetainsFailedDeliveries => false;

    internal virtual bool TryGetAttemptLimit(out int maximumAttempts)
    {
        maximumAttempts = default;
        return false;
    }

    /// <inheritdoc />
    public abstract override string ToString();

    private sealed class NoRetriesPolicy : ReminderRetryPolicy
    {
        public override string ToString() => nameof(NoRetries);
    }

    private sealed class UntilSuccessPolicy : ReminderRetryPolicy
    {
        internal override bool RetainsFailedDeliveries => true;

        public override string ToString() => nameof(UntilSuccess);
    }

    private sealed class LimitedPolicy(int maximumAttempts) : ReminderRetryPolicy
    {
        internal override bool TryGetAttemptLimit(out int limit)
        {
            limit = maximumAttempts;
            return true;
        }

        public override string ToString() => string.Concat(nameof(Limited), "(", maximumAttempts.ToString(CultureInfo.InvariantCulture), ")");
    }
}
