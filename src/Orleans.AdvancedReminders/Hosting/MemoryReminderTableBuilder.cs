using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.AdvancedReminders.Runtime.Hosting.ProviderConfiguration;
using Orleans.Hosting;
using Orleans.Providers;

[assembly: RegisterProvider("Memory", "AdvancedReminders", "Silo", typeof(AdvancedMemoryReminderTableBuilder))]

namespace Orleans.AdvancedReminders.Runtime.Hosting.ProviderConfiguration;

internal sealed class AdvancedMemoryReminderTableBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.UseInMemoryAdvancedReminderService();
    }
}
