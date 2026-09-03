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
    /// <summary>
    /// Initializes a new instance of the <see cref="SiloRedisStreamConfigurator"/> class.
    /// </summary>
    /// <param name="name">The stream provider name.</param>
    /// <param name="configureServicesDelegate">The delegate used to configure services.</param>
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
    /// Configures the Redis streaming options.
    /// </summary>
    /// <param name="configureOptions">The delegate used to configure the options.</param>
    public void ConfigureOptions(Action<RedisStreamingOptions, IServiceProvider> configureOptions) => RedisStreamingOptions.Configure(configureOptions);

    /// <summary>
    /// Gets the options builder for <see cref="RedisStreamingOptions"/>.
    /// </summary>
    public OptionsBuilder<RedisStreamingOptions> RedisStreamingOptions => this.GetNamedOptionsBuilder<RedisStreamingOptions>();

    /// <summary>
    /// Configures the Redis stream receiver options.
    /// </summary>
    /// <param name="configureOptions">The delegate used to configure the named receiver options.</param>
    /// <returns>This configurator.</returns>
    public SiloRedisStreamConfigurator ConfigureReceiver(Action<OptionsBuilder<RedisStreamReceiverOptions>> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        this.Configure(configureOptions);
        return this;
    }

    /// <summary>
    /// Configures the number of Orleans queues used to partition streams.
    /// </summary>
    /// <param name="numOfPartitions">The number of stream queues.</param>
    /// <returns>This configurator.</returns>
    public SiloRedisStreamConfigurator ConfigurePartitioning(int numOfPartitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
    {
        this.Configure<HashRingStreamQueueMapperOptions>(builder => builder.Configure(options => options.TotalQueueCount = numOfPartitions));
        return this;
    }

    /// <summary>
    /// Configures the receiver cache size.
    /// </summary>
    /// <param name="cacheSize">The cache size.</param>
    /// <returns>This configurator.</returns>
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
    /// <summary>
    /// Initializes a new instance of the <see cref="ClusterClientRedisStreamConfigurator"/> class.
    /// </summary>
    /// <param name="name">The stream provider name.</param>
    /// <param name="builder">The client builder.</param>
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
    /// Configures the Redis streaming options.
    /// </summary>
    /// <param name="configureOptions">The delegate used to configure the options.</param>
    public void ConfigureOptions(Action<RedisStreamingOptions, IServiceProvider> configureOptions) => RedisStreamingOptions.Configure(configureOptions);

    /// <summary>
    /// Gets the options builder for <see cref="RedisStreamingOptions"/>.
    /// </summary>
    public OptionsBuilder<RedisStreamingOptions> RedisStreamingOptions => this.GetNamedOptionsBuilder<RedisStreamingOptions>();

    /// <summary>
    /// Configures the number of Orleans queues used to partition streams.
    /// </summary>
    /// <param name="numOfPartitions">The number of stream queues.</param>
    /// <returns>This configurator.</returns>
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
