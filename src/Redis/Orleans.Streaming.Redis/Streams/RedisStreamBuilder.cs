using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Streaming.Redis;

namespace Orleans.Hosting;

/// <summary>
/// Configures Redis streams on a silo.
/// </summary>
public sealed class SiloRedisStreamConfigurator : SiloPersistentStreamConfigurator
{
    public SiloRedisStreamConfigurator(string name, Action<Action<IServiceCollection>> configureServicesDelegate)
        : base(name, configureServicesDelegate, RedisStreamAdapterFactory.Create)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configureServicesDelegate);

        ConfigureDelegate(services => services.ConfigureNamedOptionForLogging<RedisStreamingOptions>(name)
                .ConfigureNamedOptionForLogging<RedisStreamReceiverOptions>(name)
                .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name)
                .ConfigureNamedOptionForLogging<SimpleQueueCacheOptions>(name));
    }

    /// <summary>
    /// Gets the options builder for <see cref="RedisStreamingOptions"/>.
    /// </summary>
    public OptionsBuilder<RedisStreamingOptions> RedisStreamingOptions => this.GetNamedOptionsBuilder<RedisStreamingOptions>();

    public SiloRedisStreamConfigurator ConfigureReceiver(Action<OptionsBuilder<RedisStreamReceiverOptions>> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        this.Configure(configureOptions);
        return this;
    }

    public SiloRedisStreamConfigurator ConfigurePartitioning(int numOfPartitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
    {
        this.Configure<HashRingStreamQueueMapperOptions>(builder => builder.Configure(options => options.TotalQueueCount = numOfPartitions));
        return this;
    }

    public SiloRedisStreamConfigurator ConfigureCache(int cacheSize = SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE)
    {
        this.Configure<SimpleQueueCacheOptions>(builder => builder.Configure(options => options.CacheSize = cacheSize));
        return this;
    }
}

/// <summary>
/// Configures Redis streams on a client.
/// </summary>
public sealed class ClusterClientRedisStreamConfigurator : ClusterClientPersistentStreamConfigurator
{
    public ClusterClientRedisStreamConfigurator(string name, IClientBuilder builder)
        : base(name, builder, RedisStreamAdapterFactory.Create)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ConfigureServices(services => services.ConfigureNamedOptionForLogging<RedisStreamingOptions>(name)
                    .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name));
    }

    /// <summary>
    /// Gets the options builder for <see cref="RedisStreamingOptions"/>.
    /// </summary>
    public OptionsBuilder<RedisStreamingOptions> RedisStreamingOptions => this.GetNamedOptionsBuilder<RedisStreamingOptions>();

    public ClusterClientRedisStreamConfigurator ConfigurePartitioning(int numOfPartitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
    {
        this.Configure<HashRingStreamQueueMapperOptions>(builder => builder.Configure(options => options.TotalQueueCount = numOfPartitions));
        return this;
    }
}

file static class RedisStreamConfiguratorExtensions
{
    public static OptionsBuilder<TOptions> GetNamedOptionsBuilder<TOptions>(this INamedServiceConfigurator configurator)
        where TOptions : class, new()
    {
        ArgumentNullException.ThrowIfNull(configurator);

        OptionsBuilder<TOptions> optionsBuilder = null!;
        configurator.ConfigureDelegate(services =>
        {
            optionsBuilder = services.AddOptions<TOptions>(configurator.Name);
            services.ConfigureNamedOptionForLogging<TOptions>(configurator.Name);
        });

        return optionsBuilder;
    }
}
