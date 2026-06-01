using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Providers;
using StackExchange.Redis;

[assembly: RegisterProvider("Redis", "GrainJournaling", "Silo", typeof(Orleans.Hosting.RedisGrainJournalingProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class RedisGrainJournalingProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.AddRedisJournalStorage();
        var optionsBuilder = builder.Services.AddOptions<RedisJournalStorageOptions>();
        optionsBuilder.Configure<IServiceProvider>((options, services) =>
        {
            var serviceKey = configurationSection["ServiceKey"];
            if (!string.IsNullOrEmpty(serviceKey))
            {
                options.CreateMultiplexer = _ => Task.FromResult((services.GetRequiredKeyedService<IConnectionMultiplexer>(serviceKey), true));
            }
            else
            {
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

            var keyPrefix = configurationSection[nameof(RedisJournalStorageOptions.KeyPrefix)];
            if (!string.IsNullOrWhiteSpace(keyPrefix))
            {
                options.KeyPrefix = keyPrefix;
            }

            var compactionThresholdBytes = configurationSection[nameof(RedisJournalStorageOptions.CompactionThresholdBytes)];
            if (!string.IsNullOrWhiteSpace(compactionThresholdBytes) && long.TryParse(compactionThresholdBytes, out var parsedCompactionThresholdBytes))
            {
                options.CompactionThresholdBytes = parsedCompactionThresholdBytes;
            }
        });

        var journalFormatKey = configurationSection[nameof(JournaledStateManagerOptions.JournalFormatKey)];
        if (!string.IsNullOrWhiteSpace(journalFormatKey))
        {
            builder.Services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = journalFormatKey);
        }
    }
}
