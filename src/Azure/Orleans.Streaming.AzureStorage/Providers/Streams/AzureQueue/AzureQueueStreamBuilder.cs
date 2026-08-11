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
    public interface IAzureQueueStreamConfigurator : INamedServiceConfigurator { }

    public static class AzureQueueStreamConfiguratorExtensions
    {
        public static void ConfigureAzureQueue(this IAzureQueueStreamConfigurator configurator, Action<OptionsBuilder<AzureQueueOptions>> configureOptions)
        {
            configurator.Configure(configureOptions);
        }

        public static void ConfigureQueueDataAdapter(this IAzureQueueStreamConfigurator configurator, Func<IServiceProvider, string, IQueueDataAdapter<string, IBatchContainer>> factory)
        {
            configurator.ConfigureComponent(factory);
        }

        public static void ConfigureQueueDataAdapter<TQueueDataAdapter>(this IAzureQueueStreamConfigurator configurator)
            where TQueueDataAdapter : IQueueDataAdapter<string, IBatchContainer>
        {
            configurator.ConfigureComponent<IQueueDataAdapter<string, IBatchContainer>>((sp, n) => ActivatorUtilities.CreateInstance<TQueueDataAdapter>(sp));
        }
    }

    public interface ISiloAzureQueueStreamConfigurator : IAzureQueueStreamConfigurator, ISiloPersistentStreamConfigurator { }

    public static class SiloAzureQueueStreamConfiguratorExtensions
    {
        public static void ConfigureCacheSize(this ISiloAzureQueueStreamConfigurator configurator, int cacheSize = SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE)
        {
            configurator.Configure<SimpleQueueCacheOptions>(ob => ob.Configure(options => options.CacheSize = cacheSize));
        }
    }

    public class SiloAzureQueueStreamConfigurator : SiloPersistentStreamConfigurator, ISiloAzureQueueStreamConfigurator
    {
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

    public interface IClusterClientAzureQueueStreamConfigurator : IAzureQueueStreamConfigurator, IClusterClientPersistentStreamConfigurator { }

    public class ClusterClientAzureQueueStreamConfigurator : ClusterClientPersistentStreamConfigurator, IClusterClientAzureQueueStreamConfigurator
    {
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
