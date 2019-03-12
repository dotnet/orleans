using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Providers.Streams.Common;

namespace Orleans.Streams
{
    public interface IClusterClientPersistentStreamConfigurator : IComponentConfigurator<IClusterClientPersistentStreamConfigurator> { }

    public static class ClusterClientPersistentStreamConfiguratorExtensions
    {
        public static IClusterClientPersistentStreamConfigurator ConfigureLifecycle(this IClusterClientPersistentStreamConfigurator configurator, Action<OptionsBuilder<StreamLifecycleOptions>> configureOptions)
        {
            configurator.Configure<StreamLifecycleOptions>(configureOptions);
            return configurator;
        }

        public static IClusterClientPersistentStreamConfigurator ConfigureStreamPubSub(this IClusterClientPersistentStreamConfigurator configurator, StreamPubSubType pubsubType = StreamPubSubOptions.DEFAULT_STREAM_PUBSUB_TYPE)
        {
            configurator.Configure<StreamPubSubOptions>(ob => ob.Configure(options => options.PubSubType = pubsubType));
            return configurator;
        }
    }

    public class ClusterClientPersistentStreamConfigurator : NamedServiceConfigurator<IClusterClientPersistentStreamConfigurator>, IClusterClientPersistentStreamConfigurator
    {
        public ClusterClientPersistentStreamConfigurator(string name, IClientBuilder clientBuilder, Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory)
            : base(name, configureDelegate => clientBuilder.ConfigureServices(configureDelegate))
        {
            ConfigureComponent<IStreamProvider>(PersistentStreamProvider.Create);
            ConfigureComponent<ILifecycleParticipant<IClusterClientLifecycle>>(
                (s, n) => ((PersistentStreamProvider)s.GetRequiredServiceByName<IStreamProvider>(n)).ParticipateIn<IClusterClientLifecycle>());
            ConfigureComponent<IQueueAdapterFactory>(adapterFactory);
        }
    }
}
