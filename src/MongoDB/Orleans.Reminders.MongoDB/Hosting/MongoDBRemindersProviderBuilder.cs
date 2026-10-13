using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans;
using Orleans.Providers;
using Orleans.Providers.MongoDB.Utils;

[assembly: RegisterProvider("MongoDB", "Reminders", "Silo", typeof(Orleans.Hosting.MongoDBRemindersProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class MongoDBRemindersProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.UseMongoDBReminders(configurationSection);
        builder.Services.TryAddSingleton<IMongoClientFactory, DefaultMongoClientFactory>();
    }
}
