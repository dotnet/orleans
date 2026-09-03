using Orleans.Streaming.AdoNet;

namespace Orleans.Hosting;

/// <summary>
/// Configures an ADO.NET stream provider on a silo.
/// </summary>
public class SiloAdoNetStreamConfigurator : SiloPersistentStreamConfigurator
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SiloAdoNetStreamConfigurator"/> class.
    /// </summary>
    /// <param name="name">The stream provider name.</param>
    /// <param name="configureDelegate">The delegate used to configure services.</param>
    public SiloAdoNetStreamConfigurator(string name, Action<Action<IServiceCollection>> configureDelegate) : base(name, configureDelegate, AdoNetQueueAdapterFactory.Create)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configureDelegate);

        ConfigureDelegate(services => services
            .ConfigureNamedOptionForLogging<AdoNetStreamOptions>(name)
            .ConfigureNamedOptionForLogging<SimpleQueueCacheOptions>(name)
            .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name)
            .AddTransient<IConfigurationValidator>(sp => new AdoNetStreamOptionsValidator(sp.GetOptionsByName<AdoNetStreamOptions>(name), name)));

        // in a typical i/o bound shared database there is little benefit to more than one queue per provider
        // however multiple queues are fully supported if the user wants to fine tune throughput for their own system
        ConfigurePartitioning(1);
    }

    /// <summary>
    /// Configures the ADO.NET stream provider options.
    /// </summary>
    /// <param name="configureOptions">The delegate used to configure the named provider options.</param>
    /// <returns>This configurator.</returns>
    public SiloAdoNetStreamConfigurator ConfigureAdoNet(Action<OptionsBuilder<AdoNetStreamOptions>> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        this.Configure(configureOptions);

        return this;
    }

    /// <summary>
    /// Configures the receiver cache size.
    /// </summary>
    /// <param name="cacheSize">The cache size.</param>
    /// <returns>This configurator.</returns>
    public SiloAdoNetStreamConfigurator ConfigureCache(int cacheSize = SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE)
    {
        this.Configure<SimpleQueueCacheOptions>(ob => ob.Configure(options => options.CacheSize = cacheSize));

        return this;
    }

    /// <summary>
    /// Configures the number of Orleans queues used to partition streams.
    /// </summary>
    /// <param name="partitions">The number of stream queues.</param>
    /// <returns>This configurator.</returns>
    public SiloAdoNetStreamConfigurator ConfigurePartitioning(int partitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
    {
        this.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = partitions));

        return this;
    }
}