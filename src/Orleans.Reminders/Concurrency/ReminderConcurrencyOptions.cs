using System;

namespace Orleans.Reminders.Concurrency;

/// <summary>
/// Static configuration for reminder concurrency control. Populated via
/// <c>AddReminderConcurrencyControl</c> on the silo builder. Each tier is independently
/// optional ("pay-for-play"); a configuration with zero tiers is rejected at startup
/// rather than silently installing a no-op.
/// </summary>
public sealed class ReminderConcurrencyOptions
{
    /// <summary>
    /// In-process limit applied to all reminder dispatches on the local silo. May be null
    /// if no per-silo cap is configured.
    /// </summary>
    public ThrottleConfig? PerSilo { get; set; }

    /// <summary>
    /// Returns <c>true</c> when at least one tier has been configured.
    /// </summary>
    public bool HasAnyTier => PerSilo is not null;
}
