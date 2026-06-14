using System;

namespace Orleans.Reminders.Concurrency;

/// <summary>
/// Fluent builder used inside <c>AddReminderConcurrencyControl</c> to configure the
/// optional concurrency-control tiers.
/// </summary>
/// <remarks>
/// Phase 1 surfaces the <see cref="PerSilo(Action{ReminderThrottleConfigBuilder})"/> tier.
/// Additional tiers (global, per-grain-interface, per-reminder) are added in later phases
/// without breaking the API shape established here.
/// </remarks>
public sealed class ReminderConcurrencyBuilder
{
    private readonly ReminderConcurrencyOptions _options;

    internal ReminderConcurrencyBuilder(ReminderConcurrencyOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Configures the Per-Silo tier. Applied as a single concurrency / rate cap to every
    /// reminder tick dispatched by this silo, regardless of grain type or reminder name.
    /// </summary>
    /// <param name="configure">A delegate that configures the tier via a <see cref="ReminderThrottleConfigBuilder"/>.</param>
    public ReminderConcurrencyBuilder PerSilo(Action<ReminderThrottleConfigBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var b = new ReminderThrottleConfigBuilder();
        configure(b);
        _options.PerSilo = b.Build();
        return this;
    }
}
