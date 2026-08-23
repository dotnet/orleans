using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Streaming.RabbitMQ.Adapters;
using Orleans.Streaming.RabbitMQ.RabbitMQ;
using Orleans.Streams;

namespace Orleans.Streaming.RabbitMQ.Configurators;

/// <summary>
/// Configures a RabbitMQ persistent stream provider on an Orleans client.
/// </summary>
public class RabbitMQClientConfigurator : ClusterClientPersistentStreamConfigurator
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RabbitMQClientConfigurator"/> class.
    /// </summary>
    /// <param name="providerName">The stream provider name.</param>
    /// <param name="builder">The Orleans client builder.</param>
    public RabbitMQClientConfigurator(string providerName, IClientBuilder builder)
        : base(providerName, builder, RabbitMQAdapterFactory.Create)
    {
        builder.ConfigureServices(services =>
        {
            RabbitMQConfiguratorServices.Configure(services, providerName);
            services.AddSingleton(sp =>
                (ILifecycleParticipant<IClusterClientLifecycle>)sp.GetRequiredKeyedService<IQueueAdapterFactory>(providerName));
        });
    }

    /// <summary>
    /// Configures the RabbitMQ connection and stream options.
    /// </summary>
    /// <param name="configureOptions">The options configuration delegate.</param>
    /// <returns>This configurator.</returns>
    public RabbitMQClientConfigurator ConfigureRabbitMQ(
        Action<OptionsBuilder<RabbitMQClientOptions>> configureOptions)
    {
        this.Configure(configureOptions);
        return this;
    }

    /// <summary>
    /// Configures the Orleans stream queue count.
    /// </summary>
    /// <param name="numOfPartitions">The number of stream queues.</param>
    /// <returns>This configurator.</returns>
    public RabbitMQClientConfigurator ConfigurePartitioning(
        int numOfPartitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
    {
        this.Configure<HashRingStreamQueueMapperOptions>(options =>
            options.Configure(value => value.TotalQueueCount = numOfPartitions));
        return this;
    }
}

/// <summary>
/// Extension methods for configuring RabbitMQ persistent streams on Orleans clients.
/// </summary>
public static class ClientBuilderExtensions
{
    /// <summary>
    /// Configures an Orleans client to use RabbitMQ persistent streams.
    /// </summary>
    /// <param name="builder">The Orleans client builder.</param>
    /// <param name="name">The stream provider name.</param>
    /// <param name="configureOptions">The RabbitMQ options configuration delegate.</param>
    /// <returns>The Orleans client builder.</returns>
    public static IClientBuilder AddRabbitMQStreams(
        this IClientBuilder builder,
        string name,
        Action<RabbitMQClientOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configureOptions);

        return builder.AddRabbitMQStreams(
            name,
            configurator => configurator.ConfigureRabbitMQ(
                options => options.Configure(configureOptions)));
    }

    /// <summary>
    /// Configures an Orleans client to use RabbitMQ persistent streams.
    /// </summary>
    /// <param name="builder">The Orleans client builder.</param>
    /// <param name="name">The stream provider name.</param>
    /// <param name="configure">The provider configuration delegate.</param>
    /// <returns>The Orleans client builder.</returns>
    public static IClientBuilder AddRabbitMQStreams(
        this IClientBuilder builder,
        string name,
        Action<RabbitMQClientConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);

        var configurator = new RabbitMQClientConfigurator(name, builder);
        configure(configurator);
        return builder;
    }
}
