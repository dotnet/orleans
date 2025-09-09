using System;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Configuration;

namespace Orleans.Streaming.NATS.Hosting;

public class SiloNatsStreamConfigurator : SiloPersistentStreamConfigurator
{
    public SiloNatsStreamConfigurator(string name, Action<Action<IServiceCollection>> configureServicesDelegate)
        : base(name, configureServicesDelegate, NatsAdapterFactory.Create)
    {
        this.ConfigureDelegate(services =>
        {
            services
                .ConfigureNamedOptionForLogging<NatsOptions>(name)
                .ConfigureNamedOptionForLogging<SimpleQueueCacheOptions>(name)
                .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name)
                .AddTransient<IConfigurationValidator>(sp => new NatsStreamOptionsValidator(sp.GetRequiredService<IOptionsMonitor<NatsOptions>>().Get(name), name));
        });
    }

    public SiloNatsStreamConfigurator ConfigureNats(Action<OptionsBuilder<NatsOptions>> configureOptions)
    {
        this.Configure(configureOptions);
        return this;
    }

    public SiloNatsStreamConfigurator ConfigureCache(int cacheSize = SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE)
    {
        this.Configure<SimpleQueueCacheOptions>(ob => ob.Configure(options => options.CacheSize = cacheSize));
        return this;
    }

    public SiloNatsStreamConfigurator ConfigurePartitioning(
        int numOfparitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
    {
        this.Configure<HashRingStreamQueueMapperOptions>(ob =>
            ob.Configure(options => options.TotalQueueCount = numOfparitions));
        return this;
    }
}

public class ClusterClientNatsStreamConfigurator : ClusterClientPersistentStreamConfigurator
{
    public ClusterClientNatsStreamConfigurator(string name, IClientBuilder builder)
        : base(name, builder, NatsAdapterFactory.Create)
    {
        builder
            .ConfigureServices(services =>
            {
                services
                    .ConfigureNamedOptionForLogging<NatsOptions>(name)
                    .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name)
                    .AddTransient<IConfigurationValidator>(sp => new NatsStreamOptionsValidator(sp.GetRequiredService<IOptionsMonitor<NatsOptions>>().Get(name), name));
            });
    }

    public ClusterClientNatsStreamConfigurator ConfigureNats(Action<OptionsBuilder<NatsOptions>> configureOptions)
    {
        this.Configure(configureOptions);
        return this;
    }

    public ClusterClientNatsStreamConfigurator ConfigurePartitioning(
        int numOfparitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
    {
        this.Configure<HashRingStreamQueueMapperOptions>(ob =>
            ob.Configure(options => options.TotalQueueCount = numOfparitions));
        return this;
    }
}
