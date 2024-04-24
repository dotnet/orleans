using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Hosting;

public class SiloAdoNetStreamConfigurator : SiloPersistentStreamConfigurator
{
    public SiloAdoNetStreamConfigurator(string name, Action<Action<IServiceCollection>> configureDelegate, Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory) : base(name, configureDelegate, adapterFactory)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configureDelegate);
        ArgumentNullException.ThrowIfNull(adapterFactory);

        ConfigureDelegate(services =>
        {
            services
                .ConfigureNamedOptionForLogging<AdoNetStreamOptions>(name)
                .ConfigureNamedOptionForLogging<SimpleQueueCacheOptions>(name)
                .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name);
        });
    }

    public SiloAdoNetStreamConfigurator ConfigureAdoNet(Action<OptionsBuilder<AdoNetStreamOptions>> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        this.Configure(configureOptions);

        return this;
    }

    public SiloAdoNetStreamConfigurator ConfigureCache(int cacheSize = SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE)
    {
        this.Configure<SimpleQueueCacheOptions>(ob => ob.Configure(options => options.CacheSize = cacheSize));

        return this;
    }

    public SiloAdoNetStreamConfigurator ConfigurePartitioning(int partitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
    {
        this.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = partitions));

        return this;
    }
}