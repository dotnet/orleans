using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Providers;
using StackExchange.Redis;

[assembly: RegisterProvider("Redis", "Journaling", "Silo", typeof(Orleans.Hosting.RedisJournalingProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class RedisJournalingProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        name ??= ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME;
        builder.AddRedisJournalStorage(name, configure: null);
        var optionsBuilder = builder.Services.AddJournalStorageOptions<RedisJournalStorageOptions>(name);
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

            options.CompactionThresholdBytes = configurationSection.GetValue(nameof(RedisJournalStorageOptions.CompactionThresholdBytes), options.CompactionThresholdBytes);

            options.ReadChunkSize = configurationSection.GetValue(nameof(RedisJournalStorageOptions.ReadChunkSize), options.ReadChunkSize);

            options.InitStage = configurationSection.GetValue(nameof(RedisJournalStorageOptions.InitStage), options.InitStage);
        });

        var journalFormatKey = configurationSection[nameof(JournaledStateManagerOptions.JournalFormatKey)];
        if (!string.IsNullOrWhiteSpace(journalFormatKey))
        {
            builder.Services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = journalFormatKey);
        }
    }
}
