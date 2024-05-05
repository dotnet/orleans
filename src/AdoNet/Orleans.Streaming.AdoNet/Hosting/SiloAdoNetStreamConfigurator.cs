using Orleans.Streaming.AdoNet;

namespace Orleans.Hosting;

/// <summary>
/// Helps set up an individual stream provider on a silo.
/// </summary>
public class SiloAdoNetStreamConfigurator : SiloPersistentStreamConfigurator
{
    public SiloAdoNetStreamConfigurator(string name, Action<Action<IServiceCollection>> configureDelegate) : base(name, configureDelegate, AdoNetQueueAdapterFactory.Create)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configureDelegate);

        ConfigureDelegate(services =>
        {
            services
                .ConfigureNamedOptionForLogging<AdoNetStreamOptions>(name)
                .ConfigureNamedOptionForLogging<SimpleQueueCacheOptions>(name)
                .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name)
                .AddTransient<IConfigurationValidator>(sp => new AdoNetStreamOptionsValidator(sp.GetOptionsByName<AdoNetStreamOptions>(name), name));
        });

        // in a typical i/o bound shared database there is little benefit to more than one queue per provider
        // however multiple queues are fully supported if the user wants to fine tune throughput for their own system
        ConfigurePartitioning(1);
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