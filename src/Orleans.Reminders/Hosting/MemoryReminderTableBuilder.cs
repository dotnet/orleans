using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime.Hosting.ProviderConfiguration;

[assembly: RegisterProvider("Memory", "Reminders", "Silo", typeof(MemoryReminderTableBuilder))]

namespace Orleans.Runtime.Hosting.ProviderConfiguration;

internal sealed class MemoryReminderTableBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.UseInMemoryReminderService();
    }
}
