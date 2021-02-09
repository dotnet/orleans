using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Orleans.Hosting
{
    public interface IPersistentStreamConfigurator : INamedServiceConfigurator { }

    public static class PersistentStreamConfiguratorExtensions
    {
        public static void ConfigureStreamPubSub(this IPersistentStreamConfigurator configurator, StreamPubSubType pubsubType = StreamPubSubOptions.DEFAULT_STREAM_PUBSUB_TYPE)
        {
            configurator.Configure<StreamPubSubOptions>(ob => ob.Configure(options => options.PubSubType = pubsubType));
        }
    }

    public interface IClusterClientPersistentStreamConfigurator : IPersistentStreamConfigurator { }

    public static class ClusterClientPersistentStreamConfiguratorExtensions
    {
        public static void ConfigureLifecycle(this IClusterClientPersistentStreamConfigurator configurator, Action<OptionsBuilder<StreamLifecycleOptions>> configureOptions)
        {
            configurator.Configure(configureOptions);
        }
    }

    public class ClusterClientPersistentStreamConfigurator : NamedServiceConfigurator, IClusterClientPersistentStreamConfigurator
    {
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
