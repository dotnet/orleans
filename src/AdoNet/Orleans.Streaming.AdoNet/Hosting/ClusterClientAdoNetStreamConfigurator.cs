using Orleans.Streaming.AdoNet;

namespace Orleans.Hosting;

/// <summary>
/// Helps set up an individual stream provider on a silo.
/// </summary>
public class ClusterClientAdoNetStreamConfigurator : ClusterClientPersistentStreamConfigurator
{
    public ClusterClientAdoNetStreamConfigurator(string name, IClientBuilder clientBuilder) : base(name, clientBuilder, AdoNetQueueAdapterFactory.Create)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(clientBuilder);

        clientBuilder.ConfigureServices(services =>
        {
            services
                .ConfigureNamedOptionForLogging<AdoNetStreamOptions>(name)
                .ConfigureNamedOptionForLogging<SimpleQueueCacheOptions>(name)
                .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name)
                .AddTransient<IConfigurationValidator>(sp => new AdoNetStreamOptionsValidator(sp.GetOptionsByName<AdoNetStreamOptions>(name), name));
        });

        ConfigurePartitioning(1);
    }

    public ClusterClientAdoNetStreamConfigurator ConfigureAdoNet(Action<OptionsBuilder<AdoNetStreamOptions>> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        this.Configure(configureOptions);

        return this;
    }

    public ClusterClientAdoNetStreamConfigurator ConfigureCache(int cacheSize = SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE)
    {
        this.Configure<SimpleQueueCacheOptions>(ob => ob.Configure(options => options.CacheSize = cacheSize));

        return this;
    }

    public ClusterClientAdoNetStreamConfigurator ConfigurePartitioning(int partitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
    {
        this.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = partitions));

        return this;
    }
}