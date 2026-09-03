using System;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Streaming.EventHubs;
using Orleans.Streams;

namespace Orleans.Hosting
{
<<<<<<< HEAD
    /// <summary>
    /// Configures a named Azure Event Hubs stream provider.
    /// </summary>
    public interface IEventHubStreamConfigurator : INamedServiceConfigurator { }
||||||| parent of 82a763ec4 (style: format solution whitespace)
    public interface IEventHubStreamConfigurator : INamedServiceConfigurator {}
=======
    public interface IEventHubStreamConfigurator : INamedServiceConfigurator { }
>>>>>>> 82a763ec4 (style: format solution whitespace)

    /// <summary>
    /// Extension methods for configuring Azure Event Hubs stream providers.
    /// </summary>
    public static class EventHubStreamConfiguratorExtensions
    {
        /// <summary>
        /// Configures the Event Hub connection.
        /// </summary>
        /// <param name="configurator">The stream configurator.</param>
        /// <param name="configureOptions">The delegate used to configure Event Hub options.</param>
        public static void ConfigureEventHub(this IEventHubStreamConfigurator configurator, Action<OptionsBuilder<EventHubOptions>> configureOptions)
        {
            configurator.Configure(configureOptions);
        }

        /// <summary>
        /// Configures the adapter used to convert between Event Hubs data and Orleans stream data.
        /// </summary>
        /// <param name="configurator">The stream configurator.</param>
        /// <param name="factory">The data adapter factory.</param>
        public static void UseDataAdapter(this IEventHubStreamConfigurator configurator, Func<IServiceProvider, string, IEventHubDataAdapter> factory)
        {
            configurator.ConfigureComponent(factory);
        }
    }

    /// <summary>
    /// Configures a named Azure Event Hubs stream provider on an Orleans silo.
    /// </summary>
    public interface ISiloEventHubStreamConfigurator : IEventHubStreamConfigurator, ISiloRecoverableStreamConfigurator { }


    /// <summary>
    /// Extension methods for configuring Azure Event Hubs stream providers on an Orleans silo.
    /// </summary>
    public static class SiloEventHubStreamConfiguratorExtensions
    {
        /// <summary>
        /// Configures the factory used to create partition checkpointers.
        /// </summary>
        /// <typeparam name="TOptions">The checkpointer options type.</typeparam>
        /// <param name="configurator">The stream configurator.</param>
        /// <param name="checkpointerFactoryBuilder">The checkpointer factory builder.</param>
        /// <param name="configureOptions">The delegate used to configure checkpointer options.</param>
        public static void ConfigureCheckpointer<TOptions>(this ISiloEventHubStreamConfigurator configurator, Func<IServiceProvider, string, IStreamQueueCheckpointerFactory> checkpointerFactoryBuilder, Action<OptionsBuilder<TOptions>> configureOptions)
            where TOptions : class, new()
        {
            configurator.ConfigureComponent(checkpointerFactoryBuilder, configureOptions);
        }

        /// <summary>
        /// Configures Event Hub partition receivers.
        /// </summary>
        /// <param name="configurator">The stream configurator.</param>
        /// <param name="configureOptions">The delegate used to configure receiver options.</param>
        public static void ConfigurePartitionReceiver(this ISiloEventHubStreamConfigurator configurator, Action<OptionsBuilder<EventHubReceiverOptions>> configureOptions)
        {
            configurator.Configure(configureOptions);
        }

        /// <summary>
        /// Configures cache pressure monitoring.
        /// </summary>
        /// <param name="configurator">The stream configurator.</param>
        /// <param name="configureOptions">The delegate used to configure cache pressure options.</param>
        public static void ConfigureCachePressuring(this ISiloEventHubStreamConfigurator configurator, Action<OptionsBuilder<EventHubStreamCachePressureOptions>> configureOptions)
        {
            configurator.Configure(configureOptions);
        }

        /// <summary>
        /// Configures the stream provider to persist checkpoints using Azure Table Storage.
        /// </summary>
        /// <remarks>
        /// This compatibility method is not an extension method. Use
        /// <see cref="AzureTableStreamConfiguratorExtensions.UseAzureTableCheckpointer"/> instead.
        /// </remarks>
        /// <param name="configurator">The stream configurator.</param>
        /// <param name="configureOptions">The delegate used to configure Azure Table Storage checkpointer options.</param>
        public static void UseAzureTableCheckpointer(
            ISiloEventHubStreamConfigurator configurator,
            Action<OptionsBuilder<AzureTableStreamCheckpointerOptions>> configureOptions)
        {
            AzureTableStreamConfiguratorExtensions.UseAzureTableCheckpointer(configurator, configureOptions);
        }

    }

    /// <summary>
    /// Configures a named Azure Event Hubs stream provider on an Orleans silo.
    /// </summary>
    public class SiloEventHubStreamConfigurator : SiloRecoverableStreamConfigurator, ISiloEventHubStreamConfigurator
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SiloEventHubStreamConfigurator"/> class.
        /// </summary>
        /// <param name="name">The stream provider name.</param>
        /// <param name="configureServicesDelegate">The delegate used to configure dependency injection services.</param>
        public SiloEventHubStreamConfigurator(string name,
            Action<Action<IServiceCollection>> configureServicesDelegate)
            : base(name, configureServicesDelegate, EventHubAdapterFactory.Create)
        {
            this.ConfigureDelegate(services =>
            {
                services.AddOptions<GrainStreamQueueCheckpointerOptions>(name)
                    .Configure(static options => options.CheckpointComparer = StreamCheckpointComparers.Numeric);
                services.AddOptions<AzureTableStreamCheckpointerOptions>(name)
                    .Configure(static options =>
                    {
                        options.CheckpointComparer = StreamCheckpointComparers.Numeric;
                        options.PartitionKeyPrefix = StreamQueueCheckpointEntity.EventHubPartitionKeyPrefix;
                    });
                services.ConfigureNamedOptionForLogging<EventHubOptions>(name)
                    .ConfigureNamedOptionForLogging<EventHubReceiverOptions>(name)
                    .ConfigureNamedOptionForLogging<EventHubStreamCachePressureOptions>(name)
                    .AddTransient<IConfigurationValidator>(sp => new EventHubOptionsValidator(sp.GetOptionsByName<EventHubOptions>(name), name))
                    .AddTransient<IConfigurationValidator>(sp => new StreamCheckpointerConfigurationValidator(sp, name));
            });
        }
    }

    /// <summary>
    /// Configures a named Azure Event Hubs stream provider on an Orleans client.
    /// </summary>
    public interface IClusterClientEventHubStreamConfigurator : IEventHubStreamConfigurator, IClusterClientPersistentStreamConfigurator { }

    /// <summary>
    /// Configures a named Azure Event Hubs stream provider on an Orleans client.
    /// </summary>
    public class ClusterClientEventHubStreamConfigurator : ClusterClientPersistentStreamConfigurator, IClusterClientEventHubStreamConfigurator
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterClientEventHubStreamConfigurator"/> class.
        /// </summary>
        /// <param name="name">The stream provider name.</param>
        /// <param name="builder">The client builder.</param>
        public ClusterClientEventHubStreamConfigurator(string name, IClientBuilder builder)
           : base(name, builder, EventHubAdapterFactory.Create)
        {
            builder
                .ConfigureServices(services => services.ConfigureNamedOptionForLogging<EventHubOptions>(name)
                .AddTransient<IConfigurationValidator>(sp => new EventHubOptionsValidator(sp.GetOptionsByName<EventHubOptions>(name), name)));
        }
    }
}
