using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Streaming.Redis;

namespace Orleans.Hosting;

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

    public SiloRedisStreamConfigurator ConfigureRedis(Action<OptionsBuilder<RedisStreamingOptions>> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        this.Configure(configureOptions);
        return this;
    }

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

    public ClusterClientRedisStreamConfigurator ConfigureRedis(Action<OptionsBuilder<RedisStreamingOptions>> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        this.Configure(configureOptions);
        return this;
    }

    public ClusterClientRedisStreamConfigurator ConfigurePartitioning(int numOfPartitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
    {
        this.Configure<HashRingStreamQueueMapperOptions>(builder => builder.Configure(options => options.TotalQueueCount = numOfPartitions));
        return this;
    }
}
