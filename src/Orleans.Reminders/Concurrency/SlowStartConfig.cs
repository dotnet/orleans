using System;

namespace Orleans.Reminders.Concurrency;

/// <summary>
/// Configuration for slow-start ramp-up of reminder dispatch concurrency. Constructed via
/// <see cref="ReminderThrottleConfigBuilder.SlowStart"/>.
/// </summary>
/// <remarks>
/// <para>Slow-start mitigates cold-start thundering herds: when a silo first starts, or assumes
/// responsibility for a new range of reminders after a membership change, thousands of
/// reminders can become due at once. The reduced initial capacity lets the silo's caches,
/// connection pools, and thread pool warm up before the configured full capacity is unlocked.</para>
/// <para>Slow-start mirrors the equivalent behavior in <c>DurableJobsOptions</c>
/// (<c>SlowStartInitialConcurrency</c>, <c>SlowStartInterval</c>).</para>
/// </remarks>
public sealed class SlowStartConfig
{
    internal SlowStartConfig(int initialCapacity, TimeSpan interval, ThrottleBlockMode blockMode)
    {
        if (initialCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCapacity), initialCapacity, "Initial capacity must be greater than zero.");
        }

        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "Interval must be greater than zero.");
        }

        InitialCapacity = initialCapacity;
        Interval = interval;
        BlockMode = blockMode ?? throw new ArgumentNullException(nameof(blockMode));
    }

    /// <summary>The initial concurrency capacity available immediately after the throttle starts.</summary>
    public int InitialCapacity { get; }

    /// <summary>The interval at which the capacity is doubled until it reaches <c>MaxConcurrent</c>.</summary>
    public TimeSpan Interval { get; }

    /// <summary>How the throttle behaves when the current (ramping) capacity is exhausted but the
    /// full <c>MaxConcurrent</c> would have admitted the acquire.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><see cref="ThrottleBlockMode.Wait"/>: wait until the ramp-up releases more capacity.</item>
    /// <item><see cref="ThrottleBlockMode.WaitUpTo"/>: wait up to the configured timeout, then return <see cref="ReminderSkipReason.SlowStartLimited"/>.</item>
    /// <item><see cref="ThrottleBlockMode.SkipImmediately"/>: return <see cref="ReminderSkipReason.SlowStartLimited"/> immediately.</item>
    /// </list>
    /// </remarks>
    public ThrottleBlockMode BlockMode { get; }
}
