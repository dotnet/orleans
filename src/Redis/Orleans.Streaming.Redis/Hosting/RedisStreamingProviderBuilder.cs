using Orleans.Providers;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Hosting;
using StackExchange.Redis;
using System;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;

[assembly: RegisterProvider("Redis", "Streaming", "Silo", typeof(RedisStreamingProviderBuilder))]
[assembly: RegisterProvider("AzureRedisCache", "Streaming", "Silo", typeof(RedisStreamingProviderBuilder))]
[assembly: RegisterProvider("Redis", "Streaming", "Client", typeof(RedisStreamingProviderBuilder))]
[assembly: RegisterProvider("AzureRedisCache", "Streaming", "Client", typeof(RedisStreamingProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class RedisStreamingProviderBuilder : IProviderBuilder<ISiloBuilder>, IProviderBuilder<IClientBuilder>
{
    public void Configure(ISiloBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.AddRedisStreams(name, streamsBuilder =>
        {
            streamsBuilder.ConfigureRedis(optionsBuilder => optionsBuilder.Configure<IServiceProvider>((options, services) =>
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
                }));

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
            streamsBuilder.ConfigureRedis(optionsBuilder => optionsBuilder.Configure<IServiceProvider>((options, services) =>
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
            }));

            if (int.TryParse(configurationSection["PartitionCount"], out var partitionCount))
            {
                streamsBuilder.ConfigurePartitioning(partitionCount);
            }
        });
    }
}
