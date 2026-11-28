using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.AdvancedReminders;
using Orleans.Dashboard.Core;
using Orleans.Hosting;
using AdvancedReminderTable = Orleans.AdvancedReminders.IReminderTable;

namespace Orleans.Dashboard;

/// <summary>Registers the Advanced Reminders integration for the Orleans Dashboard.</summary>
public static class AdvancedReminderDashboardExtensions
{
    /// <summary>Exposes advanced reminder registrations in the Dashboard on a silo.</summary>
    /// <param name="builder">The silo builder with Dashboard and Advanced Reminders configured.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddAdvancedReminderDashboard(this ISiloBuilder builder)
    {
        builder.Services.TryAddSingleton<IAdvancedReminderDashboardSource>(services =>
            new AdvancedReminderDashboardSource(
                services.GetRequiredService<AdvancedReminderTable>(),
                services.GetRequiredService<IGrainFactory>().GetReminderManagementGrain()));
        return builder;
    }
}
