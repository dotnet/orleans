using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Configuration;
using Orleans.Serialization;
using Orleans.Streams;
using Orleans.Streaming.AzureStorage.Providers.Streams.AzureQueue.Json;

namespace Orleans.Hosting
{
    /// <summary>
    /// Configures an Azure Queue stream provider.
    /// </summary>
    public interface IAzureQueueStreamConfigurator : INamedServiceConfigurator { }

    /// <summary>
    /// Extension methods for configuring Azure Queue stream providers.
    /// </summary>
    public static class AzureQueueStreamConfiguratorExtensions
    {
        /// <summary>
        /// Configures the Azure Queue storage options for the stream provider.
        /// </summary>
        /// <param name="configurator">The stream provider configurator.</param>
        /// <param name="configureOptions">The delegate used to configure the Azure Queue options.</param>
        public static void ConfigureAzureQueue(this IAzureQueueStreamConfigurator configurator, Action<OptionsBuilder<AzureQueueOptions>> configureOptions)
        {
            configurator.Configure(configureOptions);
        }

        /// <summary>
        /// Configures the adapter used to convert between stream batches and Azure Queue messages.
        /// </summary>
        /// <param name="configurator">The stream provider configurator.</param>
        /// <param name="factory">The factory which creates the adapter for the named stream provider.</param>
        public static void ConfigureQueueDataAdapter(this IAzureQueueStreamConfigurator configurator, Func<IServiceProvider, string, IQueueDataAdapter<string, IBatchContainer>> factory)
        {
            configurator.ConfigureComponent(factory);
        }

        /// <summary>
        /// Configures the adapter used to convert between stream batches and Azure Queue messages.
        /// </summary>
        /// <typeparam name="TQueueDataAdapter">The data adapter type.</typeparam>
        /// <param name="configurator">The stream provider configurator.</param>
        public static void ConfigureQueueDataAdapter<TQueueDataAdapter>(this IAzureQueueStreamConfigurator configurator)
            where TQueueDataAdapter : IQueueDataAdapter<string, IBatchContainer>
        {
            configurator.ConfigureComponent<IQueueDataAdapter<string, IBatchContainer>>((sp, n) => ActivatorUtilities.CreateInstance<TQueueDataAdapter>(sp));
        }
    }

    /// <summary>
    /// Configures an Azure Queue stream provider on a silo.
    /// </summary>
    public interface ISiloAzureQueueStreamConfigurator : IAzureQueueStreamConfigurator, ISiloPersistentStreamConfigurator { }

    /// <summary>
    /// Extension methods for configuring Azure Queue stream providers on a silo.
    /// </summary>
    public static class SiloAzureQueueStreamConfiguratorExtensions
    {
        /// <summary>
        /// Configures the maximum number of stream batches held in the receiver cache.
        /// </summary>
        /// <param name="configurator">The silo stream provider configurator.</param>
        /// <param name="cacheSize">The maximum number of batches held in the cache.</param>
        public static void ConfigureCacheSize(this ISiloAzureQueueStreamConfigurator configurator, int cacheSize = SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE)
        {
            configurator.Configure<SimpleQueueCacheOptions>(ob => ob.Configure(options => options.CacheSize = cacheSize));
        }
    }

    /// <summary>
    /// Configures an Azure Queue stream provider on a silo.
    /// </summary>
    public class SiloAzureQueueStreamConfigurator : SiloPersistentStreamConfigurator, ISiloAzureQueueStreamConfigurator
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SiloAzureQueueStreamConfigurator"/> class.
        /// </summary>
        /// <param name="name">The stream provider name.</param>
        /// <param name="configureServicesDelegate">The delegate used to configure silo services.</param>
        public SiloAzureQueueStreamConfigurator(string name, Action<Action<IServiceCollection>> configureServicesDelegate)
            : base(name, configureServicesDelegate, AzureQueueAdapterFactory.Create)
        {
            this.ConfigureComponent(AzureQueueOptionsValidator.Create);
            this.ConfigureComponent(SimpleQueueCacheOptionsValidator.Create);

            //configure default queue names
            this.ConfigureAzureQueue(ob => ob.PostConfigure<IOptions<ClusterOptions>>((op, clusterOp) =>
            {
                if (op.QueueNames == null || op.QueueNames?.Count == 0)
                {
                    op.QueueNames =
                        AzureQueueStreamProviderUtils.GenerateDefaultAzureQueueNames(clusterOp.Value.ServiceId,
                            this.Name);
                }
            }));
            this.ConfigureDelegate(services => services.TryAddSingleton<IQueueDataAdapter<string, IBatchContainer>, AzureQueueDataAdapterV2>());
        }
    }

    /// <summary>
    /// Configures an Azure Queue stream provider on a cluster client.
    /// </summary>
    public interface IClusterClientAzureQueueStreamConfigurator : IAzureQueueStreamConfigurator, IClusterClientPersistentStreamConfigurator { }

    /// <summary>
    /// Configures an Azure Queue stream provider on a cluster client.
    /// </summary>
    public class ClusterClientAzureQueueStreamConfigurator : ClusterClientPersistentStreamConfigurator, IClusterClientAzureQueueStreamConfigurator
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterClientAzureQueueStreamConfigurator"/> class.
        /// </summary>
        /// <param name="name">The stream provider name.</param>
        /// <param name="builder">The client builder.</param>
        public ClusterClientAzureQueueStreamConfigurator(string name, IClientBuilder builder)
            : base(name, builder, AzureQueueAdapterFactory.Create)
        {
            this.ConfigureComponent(AzureQueueOptionsValidator.Create);

            //configure default queue names
            this.ConfigureAzureQueue(ob => ob.PostConfigure<IOptions<ClusterOptions>>((op, clusterOp) =>
            {
                if (op.QueueNames == null || op.QueueNames?.Count == 0)
                {
                    op.QueueNames =
                        AzureQueueStreamProviderUtils.GenerateDefaultAzureQueueNames(clusterOp.Value.ServiceId, this.Name);
                }
            }));
            this.ConfigureDelegate(services => services.TryAddSingleton<IQueueDataAdapter<string, IBatchContainer>, AzureQueueDataAdapterV2>());
        }
    }

    /// <summary>
    /// Silo configurator interface for Azure Queue streams with JSON serialization.
    /// This feature is experimental and subject to change in future updates.
    /// </summary>
    [Experimental("StreamingJsonSerializationExperimental", UrlFormat = "https://github.com/dotnet/orleans/pull/9618")]
    public interface ISiloAzureQueueJsonStreamConfigurator : IAzureQueueStreamConfigurator, ISiloPersistentStreamConfigurator { }

    /// <summary>
    /// Extension methods for JSON-enabled silo Azure Queue stream configurator.
    /// </summary>
    public static class SiloAzureQueueJsonStreamConfiguratorExtensions
    {
        /// <summary>
        /// Configures the cache size for the JSON-enabled Azure Queue stream provider.
        /// </summary>
        /// <param name="configurator">The configurator.</param>
        /// <param name="cacheSize">The cache size.</param>
        [Experimental("StreamingJsonSerializationExperimental", UrlFormat = "https://github.com/dotnet/orleans/pull/9618")]
        public static void ConfigureCacheSize(this ISiloAzureQueueJsonStreamConfigurator configurator, int cacheSize = SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE)
        {
            configurator.Configure<SimpleQueueCacheOptions>(ob => ob.Configure(options => options.CacheSize = cacheSize));
        }

        /// <summary>
        /// Configures JSON serializer options for the Azure Queue stream provider.
        /// </summary>
        /// <param name="configurator">The configurator.</param>
        /// <param name="configureJsonOptions">Action to configure JSON serializer options.</param>
        [Experimental("StreamingJsonSerializationExperimental", UrlFormat = "https://github.com/dotnet/orleans/pull/9618")]
        public static void ConfigureJsonSerialization(this ISiloAzureQueueJsonStreamConfigurator configurator, Action<OrleansJsonSerializerOptions> configureJsonOptions)
        {
            configurator.Configure<OrleansJsonSerializerOptions>(options => options.Configure(configureJsonOptions));
        }

        /// <summary>
        /// Configures the JSON data adapter behavior options.
        /// </summary>
        /// <param name="configurator">The configurator.</param>
        /// <param name="configureAdapterOptions">Action to configure JSON data adapter options.</param>
        [Experimental("StreamingJsonSerializationExperimental", UrlFormat = "https://github.com/dotnet/orleans/pull/9618")]
        public static void ConfigureJsonAdapter(this ISiloAzureQueueJsonStreamConfigurator configurator, Action<AzureQueueJsonDataAdapterOptions> configureAdapterOptions)
        {
            configurator.Configure<AzureQueueJsonDataAdapterOptions>(options => options.Configure(configureAdapterOptions));
        }
    }

    /// <summary>
    /// Silo configurator for Azure Queue streams with JSON serialization support.
    /// This configurator automatically sets up the JSON data adapter and required dependencies.
    /// </summary>
    [Experimental("StreamingJsonSerializationExperimental", UrlFormat = "https://github.com/dotnet/orleans/pull/9618")]
    public class SiloAzureQueueJsonStreamConfigurator : SiloPersistentStreamConfigurator, ISiloAzureQueueJsonStreamConfigurator
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SiloAzureQueueJsonStreamConfigurator"/> class.
        /// </summary>
        /// <param name="name">The stream provider name.</param>
        /// <param name="configureServicesDelegate">The delegate used to configure services.</param>
        public SiloAzureQueueJsonStreamConfigurator(string name, Action<Action<IServiceCollection>> configureServicesDelegate)
            : base(name, configureServicesDelegate, AzureQueueAdapterFactory.Create)
        {
            this.ConfigureComponent(AzureQueueOptionsValidator.Create);
            this.ConfigureComponent(SimpleQueueCacheOptionsValidator.Create);

            // Configure default queue names
            this.ConfigureAzureQueue(ob => ob.PostConfigure<IOptions<ClusterOptions>>((op, clusterOp) =>
            {
                if (op.QueueNames == null || op.QueueNames?.Count == 0)
                {
                    op.QueueNames =
                        AzureQueueStreamProviderUtils.GenerateDefaultAzureQueueNames(clusterOp.Value.ServiceId,
                            this.Name);
                }
            }));

            this.Configure<OrleansJsonSerializerOptions>(options => { });
            this.Configure<AzureQueueJsonDataAdapterOptions>(options => { });
            this.ConfigureQueueDataAdapter(AzureQueueJsonDataAdapter.Create);
        }
    }

    /// <summary>
    /// Cluster client configurator interface for Azure Queue streams with JSON serialization.
    /// This feature is experimental and subject to change in future updates.
    /// </summary>
    [Experimental("StreamingJsonSerializationExperimental", UrlFormat = "https://github.com/dotnet/orleans/pull/9618")]
    public interface IClusterClientAzureQueueJsonStreamConfigurator : IAzureQueueStreamConfigurator, IClusterClientPersistentStreamConfigurator { }

    /// <summary>
    /// Extension methods for JSON-enabled cluster client Azure Queue stream configurator.
    /// </summary>
    public static class ClusterClientAzureQueueJsonStreamConfiguratorExtensions
    {
        /// <summary>
        /// Configures JSON serializer options for the Azure Queue stream provider.
        /// </summary>
        /// <param name="configurator">The configurator.</param>
        /// <param name="configureJsonOptions">Action to configure JSON serializer options.</param>
        [Experimental("StreamingJsonSerializationExperimental", UrlFormat = "https://github.com/dotnet/orleans/pull/9618")]
        public static void ConfigureJsonSerialization(this IClusterClientAzureQueueJsonStreamConfigurator configurator, Action<OrleansJsonSerializerOptions> configureJsonOptions)
        {
            configurator.Configure<OrleansJsonSerializerOptions>(options => options.Configure(configureJsonOptions));
        }

        /// <summary>
        /// Configures the JSON data adapter behavior options.
        /// </summary>
        /// <param name="configurator">The configurator.</param>
        /// <param name="configureAdapterOptions">Action to configure JSON data adapter options.</param>
        [Experimental("StreamingJsonSerializationExperimental", UrlFormat = "https://github.com/dotnet/orleans/pull/9618")]
        public static void ConfigureJsonAdapter(this IClusterClientAzureQueueJsonStreamConfigurator configurator, Action<AzureQueueJsonDataAdapterOptions> configureAdapterOptions)
        {
            configurator.Configure<AzureQueueJsonDataAdapterOptions>(options => options.Configure(configureAdapterOptions));
        }
    }

    /// <summary>
    /// Cluster client configurator for Azure Queue streams with JSON serialization support.
    /// This configurator automatically sets up the JSON data adapter and required dependencies.
    /// </summary>
    [Experimental("StreamingJsonSerializationExperimental", UrlFormat = "https://github.com/dotnet/orleans/pull/9618")]
    public class ClusterClientAzureQueueJsonStreamConfigurator : ClusterClientPersistentStreamConfigurator, IClusterClientAzureQueueJsonStreamConfigurator
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterClientAzureQueueJsonStreamConfigurator"/> class.
        /// </summary>
        /// <param name="name">The stream provider name.</param>
        /// <param name="builder">The client builder.</param>
        public ClusterClientAzureQueueJsonStreamConfigurator(string name, IClientBuilder builder)
            : base(name, builder, AzureQueueAdapterFactory.Create)
        {
            this.ConfigureComponent(AzureQueueOptionsValidator.Create);

            // Configure default queue names
            this.ConfigureAzureQueue(ob => ob.PostConfigure<IOptions<ClusterOptions>>((op, clusterOp) =>
            {
                if (op.QueueNames == null || op.QueueNames?.Count == 0)
                {
                    op.QueueNames =
                        AzureQueueStreamProviderUtils.GenerateDefaultAzureQueueNames(clusterOp.Value.ServiceId, this.Name);
                }
            }));

            this.Configure<OrleansJsonSerializerOptions>(options => { });
            this.Configure<AzureQueueJsonDataAdapterOptions>(options => { });
            this.ConfigureQueueDataAdapter(AzureQueueJsonDataAdapter.Create);
        }
    }
}
