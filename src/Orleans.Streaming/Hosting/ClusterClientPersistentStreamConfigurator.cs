using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Orleans.Hosting
{
    /// <summary>
    /// Configuration builder for persistent streams.
    /// </summary>
    public interface IPersistentStreamConfigurator : INamedServiceConfigurator { }

    /// <summary>
    /// Extension methods for <see cref="IPersistentStreamConfigurator"/>.
    /// </summary>
    public static class PersistentStreamConfiguratorExtensions
    {
        /// <summary>
        /// Configures the stream pub/sub type.
        /// </summary>
        /// <param name="configurator">The configuration builder.</param>
        /// <param name="pubsubType">The stream pub/sub type to use.</param>
        public static void ConfigureStreamPubSub(this IPersistentStreamConfigurator configurator, StreamPubSubType pubsubType = StreamPubSubOptions.DEFAULT_STREAM_PUBSUB_TYPE)
        {
            configurator.Configure<StreamPubSubOptions>(ob => ob.Configure(options => options.PubSubType = pubsubType));
        }
    }

    /// <summary>
    /// Client-specific configuration builder for persistent stream.
    /// </summary>
    public interface IClusterClientPersistentStreamConfigurator : IPersistentStreamConfigurator { }

    /// <summary>
    /// Extension methods for <see cref="IClusterClientPersistentStreamConfigurator"/>.
    /// </summary>
    public static class ClusterClientPersistentStreamConfiguratorExtensions
    {
        /// <summary>
        /// Configures the <see cref="StreamLifecycleOptions"/>.
        /// </summary>
        /// <param name="configurator">The configuration builder.</param>
        /// <param name="configureOptions">The configuration delegate.</param>
        public static void ConfigureLifecycle(this IClusterClientPersistentStreamConfigurator configurator, Action<OptionsBuilder<StreamLifecycleOptions>> configureOptions)
        {
            configurator.Configure(configureOptions);
        }
    }

    /// <summary>
    /// Client-side configuration provider for persistent streams.
    /// </summary>
    public class ClusterClientPersistentStreamConfigurator : NamedServiceConfigurator, IClusterClientPersistentStreamConfigurator
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterClientPersistentStreamConfigurator"/> class.
        /// </summary>
        /// <param name="name">The stream provider name.</param>
        /// <param name="clientBuilder">The client builder.</param>
        /// <param name="adapterFactory">The adapter factory.</param>
        public ClusterClientPersistentStreamConfigurator(string name, IClientBuilder clientBuilder, Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory)
            : base(name, configureDelegate => clientBuilder.ConfigureServices(configureDelegate))
        {
            clientBuilder.AddStreaming();
            this.ConfigureComponent(PersistentStreamProvider.Create);
            this.ConfigureComponent(PersistentStreamProvider.ParticipateIn<IClusterClientLifecycle>);
            this.ConfigureComponent(adapterFactory);
        }
    }
}
