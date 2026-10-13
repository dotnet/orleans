using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Providers;

[assembly: RegisterProvider("MongoDB", "Reminders", "Silo", typeof(Orleans.Hosting.MongoDBRemindersProviderBuilder))]
[assembly: RegisterProvider("MongoDBDatabase", "Reminders", "Silo", typeof(Orleans.Hosting.MongoDBRemindersProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class MongoDBRemindersProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.UseMongoDBReminders(configurationSection);
    }
}
