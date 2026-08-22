using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streaming.RabbitMQ.Adapters;
using Orleans.Streaming.RabbitMQ.RabbitMQ;
using Orleans.Streams;

namespace Orleans.Streaming.RabbitMQ.Configurators;

public class RabbitMQSiloConfigurator : SiloPersistentStreamConfigurator
{
    public RabbitMQSiloConfigurator(string providerName, Action<Action<IServiceCollection>> configureDelegate) : base(
        providerName, configureDelegate, RabbitMQAdapterFactory.Create)
    {
        ConfigureDelegate(services =>
        {
            services
                .AddKeyedSingleton<RabbitMQStreamSystemProvider>(providerName, (sp, _) =>
                    new RabbitMQStreamSystemProvider(
                        sp.GetOptionsByName<RabbitMQClientOptions>(providerName),
                        sp.GetRequiredService<ILogger<RabbitMQStreamSystemProvider>>()))
                .AddKeyedSingleton<RabbitMQQueueProvider>(providerName, (sp, _) =>
                    new RabbitMQQueueProvider(
                        sp.GetRequiredKeyedService<RabbitMQStreamSystemProvider>(providerName),
                        providerName,
                        sp.GetOptionsByName<RabbitMQClientOptions>(providerName)))
                .AddKeyedSingleton<RabbitMQAdapterReceiverFactory>(providerName, (sp, _) =>
                    new RabbitMQAdapterReceiverFactory(
                        sp.GetRequiredService<ILoggerFactory>(),
                        sp.GetRequiredService<Serializer>(),
                        sp.GetOptionsByName<RabbitMQClientOptions>(providerName),
                        sp.GetRequiredService<OrleansInstruments>()))
                .AddTransient<IConfigurationValidator>(sp =>
                    new RabbitMQStreamOptionsValidator(
                        sp.GetOptionsByName<StreamPullingAgentOptions>(providerName),
                        providerName))
                .ConfigureFormatterResolver<RabbitMQClientOptions, RabbitMQClientOptionsFormatterResolver>()
                .ConfigureNamedOptionForLogging<RabbitMQClientOptions>(providerName)
                .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(providerName);
            services.AddSingleton(sp =>
                (ILifecycleParticipant<ISiloLifecycle>)sp.GetRequiredKeyedService<IQueueAdapterFactory>(providerName));
        });
    }

    public RabbitMQSiloConfigurator ConfigureOffsetUpdateInterval(TimeSpan interval)
    {
        this.Configure<RabbitMQClientOptions>(opt => opt.Configure(e => e.IntervalToUpdateOffset = interval));
        return this;
    }

    public RabbitMQSiloConfigurator ConfigureRabbitMQ(Action<OptionsBuilder<RabbitMQClientOptions>> configureOptions)
    {
        this.Configure(configureOptions);
        return this;
    }

    public RabbitMQSiloConfigurator ConfigureCache(int cacheSize = RabbitMQQueueCacheOptions.DEFAULT_CACHE_SIZE)
    {
        this.Configure<RabbitMQQueueCacheOptions>(ob => ob.Configure(options => options.CacheSize = cacheSize));
        return this;
    }

    public RabbitMQSiloConfigurator ConfigurePartitioning(
        int numOfparitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
    {
        this.Configure<HashRingStreamQueueMapperOptions>(ob =>
            ob.Configure(options => options.TotalQueueCount = numOfparitions));
        return this;
    }
}

public static class SiloBuilderExtensions
{
    /// <summary>
    ///     Configure silo to use RabbitMQ persistent streams.
    /// </summary>
    public static ISiloBuilder AddRabbitMQStreams(this ISiloBuilder builder, string name,
        Action<RabbitMQClientOptions> configureOptions)
    {
        builder.AddRabbitMQStreams(name, b => b.ConfigureRabbitMQ(ob => ob.Configure(configureOptions)));
        return builder;
    }

    /// <summary>
    ///     Configure silo to use RabbitMQ persistent streams.
    /// </summary>
    public static ISiloBuilder AddRabbitMQStreams(this ISiloBuilder builder, string name,
        Action<RabbitMQSiloConfigurator> configure = null)
    {
        var configurator = new RabbitMQSiloConfigurator(name,
            configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate));
        configure?.Invoke(configurator);
        return builder;
    }
}