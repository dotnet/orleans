using System;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Configuration;

namespace Orleans.Streaming.NATS.Hosting;

/// <summary>
/// Configures a NATS JetStream provider on a silo.
/// </summary>
public class SiloNatsStreamConfigurator : SiloPersistentStreamConfigurator
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SiloNatsStreamConfigurator"/> class.
    /// </summary>
    /// <param name="name">The stream provider name.</param>
    /// <param name="configureServicesDelegate">The delegate used to configure services.</param>
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

    /// <summary>
    /// Configures the NATS JetStream provider options.
    /// </summary>
    /// <param name="configureOptions">The delegate used to configure the named provider options.</param>
    /// <returns>This configurator.</returns>
    public SiloNatsStreamConfigurator ConfigureNats(Action<OptionsBuilder<NatsOptions>> configureOptions)
    {
        this.Configure(configureOptions);
        return this;
    }

    /// <summary>
    /// Configures the receiver cache size.
    /// </summary>
    /// <param name="cacheSize">The cache size.</param>
    /// <returns>This configurator.</returns>
    public SiloNatsStreamConfigurator ConfigureCache(int cacheSize = SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE)
    {
        this.Configure<SimpleQueueCacheOptions>(ob => ob.Configure(options => options.CacheSize = cacheSize));
        return this;
    }

    /// <summary>
    /// Configures the number of Orleans queues used to partition streams.
    /// </summary>
    /// <param name="numOfparitions">The number of stream queues.</param>
    /// <returns>This configurator.</returns>
    public SiloNatsStreamConfigurator ConfigurePartitioning(
        int numOfparitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
    {
        this.Configure<HashRingStreamQueueMapperOptions>(ob =>
            ob.Configure(options => options.TotalQueueCount = numOfparitions));
        return this;
    }
}

/// <summary>
/// Configures a NATS JetStream provider on a client.
/// </summary>
public class ClusterClientNatsStreamConfigurator : ClusterClientPersistentStreamConfigurator
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ClusterClientNatsStreamConfigurator"/> class.
    /// </summary>
    /// <param name="name">The stream provider name.</param>
    /// <param name="builder">The client builder.</param>
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

    /// <summary>
    /// Configures the NATS JetStream provider options.
    /// </summary>
    /// <param name="configureOptions">The delegate used to configure the named provider options.</param>
    /// <returns>This configurator.</returns>
    public ClusterClientNatsStreamConfigurator ConfigureNats(Action<OptionsBuilder<NatsOptions>> configureOptions)
    {
        this.Configure(configureOptions);
        return this;
    }

    /// <summary>
    /// Configures the number of Orleans queues used to partition streams.
    /// </summary>
    /// <param name="numOfparitions">The number of stream queues.</param>
    /// <returns>This configurator.</returns>
    public ClusterClientNatsStreamConfigurator ConfigurePartitioning(
        int numOfparitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
    {
        this.Configure<HashRingStreamQueueMapperOptions>(ob =>
            ob.Configure(options => options.TotalQueueCount = numOfparitions));
        return this;
    }
}
