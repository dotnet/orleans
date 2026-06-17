using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration.Internal;
using Orleans.Runtime.Messaging;
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
                    LogConfiguredTier(logger, "per-silo", perSilo);

                    IOverloadDetector? overloadDetector = null;
                    if (perSilo.Overload is not null)
                    {
                        overloadDetector = sp.GetService<IOverloadDetector>()
                            ?? throw new OrleansConfigurationException(
                                "Reminder concurrency control was configured with RespectOverload, " +
                                "but no IOverloadDetector was found in the silo service collection. " +
                                "Ensure that the silo is properly configured (IOverloadDetector is " +
                                "registered by default in DefaultSiloServices), or remove the " +
                                "RespectOverload configuration.");
                    }

                    return new LocalReminderDeliveryThrottle(perSilo, timeProvider, tierName: "per-silo", overloadDetector);
                }

                // The validator rejects zero-tier configurations, so this should be unreachable;
                // keep a safe fallback regardless.
                return NoOpReminderDeliveryThrottle.Instance;
            });
        });
    }

    private static void LogConfiguredTier(ILogger logger, string tier, ThrottleConfig config)
    {
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        logger.LogInformation(
            "Reminder concurrency control configured: tier={Tier} defaultBlockMode={DefaultBlockMode} localConcurrency={LocalConcurrency} localRate={LocalRate} respectOverload={RespectOverload} slowStart={SlowStart}",
            tier,
            config.BlockMode.GetType().Name,
            config.Concurrency is null ? "disabled" : $"maxConcurrent={config.Concurrency.MaxConcurrent}/{config.Concurrency.BlockMode.GetType().Name}",
            config.Rate is null ? "disabled" : $"permitsPerSecond={config.Rate.PermitsPerSecond:0.##}/burstSize={config.Rate.BurstSize}/{config.Rate.BlockMode.GetType().Name}",
            config.Overload is null ? "disabled" : $"{config.Overload.BlockMode.GetType().Name}/pollInterval={config.Overload.PollInterval}",
            config.SlowStart is null ? "disabled" : $"initial={config.SlowStart.InitialCapacity}/interval={config.SlowStart.Interval}/{config.SlowStart.BlockMode.GetType().Name}");
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
