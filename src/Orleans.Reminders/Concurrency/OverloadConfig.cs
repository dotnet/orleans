using System;
using Orleans.Runtime.Messaging;

namespace Orleans.Reminders.Concurrency;

/// <summary>
/// Configuration for reminder dispatch backoff in response to silo overload, as reported by
/// <see cref="IOverloadDetector"/>. Constructed via
/// <see cref="ReminderThrottleConfigBuilder.RespectOverload"/>.
/// </summary>
/// <remarks>
/// <see cref="IOverloadDetector"/> is a cluster-wide cross-cutting signal already honored by
/// the silo gateway, placement directors, and Durable Jobs. Opting in to <c>RespectOverload</c>
/// on a reminder throttle keeps reminder dispatch consistent with those other dispatch paths
/// during silo CPU/memory pressure.
/// </remarks>
public sealed class OverloadConfig
{
    internal OverloadConfig(ThrottleBlockMode blockMode, TimeSpan pollInterval)
    {
        BlockMode = blockMode ?? throw new ArgumentNullException(nameof(blockMode));
        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), pollInterval, "Poll interval must be greater than zero.");
        }

        PollInterval = pollInterval;
    }

    /// <summary>How the throttle behaves while the silo is reported as overloaded.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><see cref="ThrottleBlockMode.Wait"/>: poll the overload signal at <see cref="PollInterval"/> until it clears.</item>
    /// <item><see cref="ThrottleBlockMode.WaitUpTo"/>: poll up to the configured timeout, then return <see cref="ReminderSkipReason.SiloOverloaded"/>.</item>
    /// <item><see cref="ThrottleBlockMode.SkipImmediately"/>: return <see cref="ReminderSkipReason.SiloOverloaded"/> immediately.</item>
    /// </list>
    /// </remarks>
    public ThrottleBlockMode BlockMode { get; }

    /// <summary>
    /// How often the overload signal is re-checked while the throttle is waiting for the silo
    /// to recover. Default 1 second, matching the conservative end of similar polling loops
    /// elsewhere in Orleans.
    /// </summary>
    public TimeSpan PollInterval { get; }
}
