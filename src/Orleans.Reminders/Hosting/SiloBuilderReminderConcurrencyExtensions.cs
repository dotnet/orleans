using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration.Internal;
using Orleans.Reminders.Concurrency;
using Orleans.Runtime;

namespace Orleans.Hosting;

/// <summary>
/// Silo-builder extensions for opt-in reminder concurrency control.
/// </summary>
public static class SiloBuilderReminderConcurrencyExtensions
{
    /// <summary>
    /// Enables concurrency control for reminder deliveries on this silo.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configure">A delegate that configures one or more throttle tiers.</param>
    /// <returns>The silo builder.</returns>
    /// <remarks>
    /// <para>This method must be called on a silo builder that has already been configured for
    /// reminders (via <c>AddReminders</c> or one of the per-provider extensions such as
    /// <c>UseAzureTableReminderService</c>).</para>
    /// <para>Calling this method with zero configured tiers is a startup error rather than a
    /// silent no-op: a misconfiguration that disables every tier is almost always a bug
    /// that should fail fast.</para>
    /// </remarks>
    public static ISiloBuilder AddReminderConcurrencyControl(this ISiloBuilder builder, Action<ReminderConcurrencyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        return builder.ConfigureServices(services =>
        {
            services.AddOptions<ReminderConcurrencyOptions>().Configure(opts =>
            {
                var b = new ReminderConcurrencyBuilder(opts);
                configure(b);
            });

            services.TryAddSingleton<ReminderThrottleInstruments>();
            services.AddSingleton<IConfigurationValidator, ReminderConcurrencyOptionsValidator>();

            // Replace the default no-op throttle with a configured composite (Phase 1: just PerSilo).
            services.RemoveAll<IReminderDeliveryThrottle>();
            services.AddSingleton<IReminderDeliveryThrottle>(sp =>
            {
                var opts = sp.GetRequiredService<IOptions<ReminderConcurrencyOptions>>().Value;
                var timeProvider = sp.GetRequiredService<TimeProvider>();
                var logger = sp.GetRequiredService<ILogger<LocalReminderDeliveryThrottle>>();

                if (opts.PerSilo is { } perSilo)
                {
                    LogConfiguredTier(logger, "per-silo", perSilo.MaxConcurrent, perSilo.PermitsPerSecond, perSilo.BurstSize, perSilo.BlockMode.GetType().Name);
                    return new LocalReminderDeliveryThrottle(perSilo, timeProvider, tierName: "per-silo");
                }

                // The validator rejects zero-tier configurations, so this should be unreachable;
                // keep a safe fallback regardless.
                return NoOpReminderDeliveryThrottle.Instance;
            });
        });
    }

    private static void LogConfiguredTier(ILogger logger, string tier, int? maxConcurrent, double? permitsPerSecond, int? burstSize, string blockMode)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Reminder concurrency control configured: tier={Tier} maxConcurrent={MaxConcurrent} permitsPerSecond={PermitsPerSecond} burstSize={BurstSize} blockMode={BlockMode}",
                tier,
                maxConcurrent?.ToString() ?? "unlimited",
                permitsPerSecond?.ToString("0.##") ?? "unlimited",
                burstSize?.ToString() ?? "n/a",
                blockMode);
        }
    }
}

/// <summary>
/// Startup validator for <see cref="ReminderConcurrencyOptions"/>. Rejects configurations
/// that do not configure at least one tier.
/// </summary>
internal sealed class ReminderConcurrencyOptionsValidator : IConfigurationValidator
{
    private readonly IOptions<ReminderConcurrencyOptions> _options;

    public ReminderConcurrencyOptionsValidator(IOptions<ReminderConcurrencyOptions> options)
    {
        _options = options;
    }

    public void ValidateConfiguration()
    {
        if (!_options.Value.HasAnyTier)
        {
            throw new OrleansConfigurationException(
                "AddReminderConcurrencyControl was called but no tiers were configured. " +
                "Configure at least one tier (for example, .PerSilo(t => t.MaxConcurrent(50))), " +
                "or remove the AddReminderConcurrencyControl call.");
        }
    }
}
