using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;
using StackExchange.Redis;

#nullable disable
[assembly: RegisterProvider("Redis", "Streaming", "Silo", typeof(RedisStreamProviderBuilder))]
[assembly: RegisterProvider("AzureRedisCache", "Streaming", "Silo", typeof(RedisStreamProviderBuilder))]
[assembly: RegisterProvider("Redis", "Streaming", "Client", typeof(RedisStreamProviderBuilder))]
[assembly: RegisterProvider("AzureRedisCache", "Streaming", "Client", typeof(RedisStreamProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class RedisStreamProviderBuilder : IProviderBuilder<ISiloBuilder>, IProviderBuilder<IClientBuilder>
{
    public void Configure(ISiloBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.AddRedisStreams(name, streamsBuilder =>
        {
            streamsBuilder.ConfigureRedis(GetOptionsBuilder(configurationSection));

            if (int.TryParse(configurationSection["PartitionCount"], out var partitionCount))
            {
                streamsBuilder.ConfigurePartitioning(partitionCount);
            }
        });
    }

    public void Configure(IClientBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.AddRedisStreams(name, streamsBuilder =>
        {
            streamsBuilder.ConfigureRedis(GetOptionsBuilder(configurationSection));

            if (int.TryParse(configurationSection["PartitionCount"], out var partitionCount))
            {
                streamsBuilder.ConfigurePartitioning(partitionCount);
            }
        });
    }

    private static Action<OptionsBuilder<RedisStreamOptions>> GetOptionsBuilder(IConfigurationSection configurationSection)
    {
        return optionsBuilder => optionsBuilder.Configure<IServiceProvider>((options, services) =>
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
