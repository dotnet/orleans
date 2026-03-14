using Orleans.Providers;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Hosting;
using Orleans.AdvancedReminders.Redis;
using StackExchange.Redis;
using System;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;

[assembly: RegisterProvider("Redis", "AdvancedReminders", "Silo", typeof(AdvancedRedisRemindersProviderBuilder))]
[assembly: RegisterProvider("AzureRedisCache", "AdvancedReminders", "Silo", typeof(AdvancedRedisRemindersProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class AdvancedRedisRemindersProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.UseRedisAdvancedReminderService(_ => { });
        builder.Services.AddOptions<RedisReminderTableOptions>()
            .Configure<IServiceProvider>((options, services) =>
            {
                var serviceKey = configurationSection["ServiceKey"];
                if (!string.IsNullOrEmpty(serviceKey))
                {
                    // Get a connection multiplexer instance by name.
                    var multiplexer = services.GetRequiredKeyedService<IConnectionMultiplexer>(serviceKey);
                    options.CreateMultiplexer = _ => Task.FromResult(multiplexer);
                    options.ConfigurationOptions = new ConfigurationOptions();
                }
                else
                {
                    // Construct a connection multiplexer from a connection string.
                    var connectionName = configurationSection["ConnectionName"];
                    var connectionString = configurationSection["ConnectionString"];
                    if (!string.IsNullOrEmpty(connectionName) && string.IsNullOrEmpty(connectionString))
                    {
                        var rootConfiguration = services.GetRequiredService<IConfiguration>();
                        connectionString = rootConfiguration.GetConnectionString(connectionName);
                    }

                    if (!string.IsNullOrEmpty(connectionString))
                    {
                        options.ConfigurationOptions = ConfigurationOptions.Parse(connectionString);
                    }
                }
            });
    }
}
