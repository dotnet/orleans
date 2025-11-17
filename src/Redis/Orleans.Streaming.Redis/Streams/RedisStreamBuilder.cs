using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Streaming.Redis.Streams;
using Orleans.Streams;

namespace Orleans.Hosting;

public class SiloRedisStreamConfigurator : SiloPersistentStreamConfigurator
{
    public SiloRedisStreamConfigurator(string name, ISiloBuilder siloBuilder) :
        base(name, configureDelegate => siloBuilder.ConfigureServices(configureDelegate), RedisStreamAdapterFactory.Create)
    {
        ConfigureDelegate(services =>
        {
            services
                .ConfigureNamedOptionForLogging<RedisStreamOptions>(name)
                .ConfigureNamedOptionForLogging<SimpleQueueCacheOptions>(name)
                .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name);
        });
    }

    public SiloRedisStreamConfigurator ConfigureRedis(Action<OptionsBuilder<RedisStreamOptions>> configureOptions)
    {
        this.Configure(configureOptions);
        return this;
    }

    public SiloRedisStreamConfigurator ConfigureCache(int cacheSize = SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE)
    {
        this.Configure<SimpleQueueCacheOptions>(ob => ob.Configure(options => options.CacheSize = cacheSize));
        return this;
    }

    public SiloRedisStreamConfigurator ConfigurePartitioning(int numOfparitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
    {
        this.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = numOfparitions));
        return this;
    }

    public SiloRedisStreamConfigurator ConfigureQueueDataAdapter(Func<IServiceProvider, string, IQueueDataAdapter<string, IBatchContainer>> factory)
    {
        this.ConfigureComponent(factory);
        return this;
    }

    public SiloRedisStreamConfigurator ConfigureQueueDataAdapter<TQueueDataAdapter>()
        where TQueueDataAdapter : class, IQueueDataAdapter<string, IBatchContainer>
    {
        this.ConfigureComponent<IQueueDataAdapter<string, IBatchContainer>>((sp, n) => ActivatorUtilities.CreateInstance<TQueueDataAdapter>(sp));
        return this;
    }

    internal void PostConfigureComponents()
    {
        ConfigureDelegate(services => RedisStreamAdapterFactory.PostConfigureDefaults(services, Name));
    }
}

public class ClusterClientRedisStreamConfigurator : ClusterClientPersistentStreamConfigurator
{
    public ClusterClientRedisStreamConfigurator(string name, IClientBuilder clientBuilder)
        : base(name, clientBuilder, RedisStreamAdapterFactory.Create)
    {
        ConfigureDelegate(services =>
        {
            services
                .ConfigureNamedOptionForLogging<RedisStreamOptions>(name)
                .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name);
        });
    }

    public ClusterClientRedisStreamConfigurator ConfigureRedis(Action<OptionsBuilder<RedisStreamOptions>> configureOptions)
    {
        this.Configure(configureOptions);
        return this;

    }

    public ClusterClientRedisStreamConfigurator ConfigurePartitioning(int numOfparitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
    {
        this.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = numOfparitions));
        return this;
    }

    public ClusterClientRedisStreamConfigurator ConfigureQueueDataAdapter(Func<IServiceProvider, string, IQueueDataAdapter<string, IBatchContainer>> factory)
    {
        this.ConfigureComponent(factory);
        return this;
    }

    public ClusterClientRedisStreamConfigurator ConfigureQueueDataAdapter<TQueueDataAdapter>()
        where TQueueDataAdapter : class, IQueueDataAdapter<string, IBatchContainer>
    {
        this.ConfigureComponent<IQueueDataAdapter<string, IBatchContainer>>((sp, n) => ActivatorUtilities.CreateInstance<TQueueDataAdapter>(sp));
        return this;
    }

    internal void PostConfigureComponents()
    {
        ConfigureDelegate(services => RedisStreamAdapterFactory.PostConfigureDefaults(services, Name));
    }
}

