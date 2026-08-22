using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Orleans.Reminders.TestKit;

/// <summary>
/// Installs the <see cref="IdealizedReminderTable"/> oracle into an <see cref="InProcessTestCluster"/>.
/// </summary>
/// <remarks>
/// A single oracle instance is shared by every silo in the cluster, mirroring the way the in-process cluster shares
/// one grain directory and one membership table across silos. Sharing one instance is what makes cluster-level
/// reminder behaviour deterministically observable: every silo's reminder service reads and writes the same
/// introspectable state, and the test controls (blocking gates, injected failures, outage simulation and frozen
/// reads) apply uniformly across the cluster.
/// </remarks>
public static class ReminderTestKitClusterBuilderExtensions
{
    /// <summary>
    /// Installs a shared <see cref="IdealizedReminderTable"/> as the reminder table of every silo in the cluster.
    /// </summary>
    /// <param name="builder">The test cluster builder.</param>
    /// <param name="table">An existing oracle to install, or <see langword="null"/> to create one.</param>
    /// <param name="reminderTimeProvider">
    /// An optional deterministic clock registered as the reminder subsystem's keyed
    /// <see cref="TimeProvider"/> (<see cref="ReminderTimeProviderNames.Reminders"/>). Unrelated silo timers keep
    /// using the ambient provider.
    /// </param>
    /// <param name="configureReminderOptions">An optional <see cref="ReminderOptions"/> post-configuration callback.</param>
    /// <returns>The installed oracle, which exposes the deterministic test controls and introspection.</returns>
    public static IdealizedReminderTable UseIdealizedReminderTable(
        this InProcessTestClusterBuilder builder,
        IdealizedReminderTable? table = null,
        TimeProvider? reminderTimeProvider = null,
        Action<ReminderOptions>? configureReminderOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var oracle = table ?? new IdealizedReminderTable();
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.UseIdealizedReminderTable(oracle);

            if (reminderTimeProvider is not null)
            {
                siloBuilder.Services.AddKeyedSingleton(ReminderTimeProviderNames.Reminders, reminderTimeProvider);
            }

            if (configureReminderOptions is not null)
            {
                siloBuilder.Services.PostConfigure(configureReminderOptions);
            }
        });

        return oracle;
    }

    /// <summary>
    /// Installs the supplied <see cref="IdealizedReminderTable"/> as this silo's reminder table.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="table">The oracle to install.</param>
    /// <returns>The silo builder, for chaining.</returns>
    public static ISiloBuilder UseIdealizedReminderTable(this ISiloBuilder builder, IdealizedReminderTable table)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(table);

        builder.AddReminders();
        builder.Services.AddSingleton(table);
        builder.Services.AddSingleton<IReminderTable>(table);
        return builder;
    }
}
