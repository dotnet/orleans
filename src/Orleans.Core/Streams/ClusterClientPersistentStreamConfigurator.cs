using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;

namespace Orleans.Streams
{
    public interface IClusterClientPersistentStreamConfigurator { }

    public static class ClusterClientPersistentStreamConfiguratorExtensions
    {
        public static TConfigurator ConfigureLifecycle<TConfigurator>(this TConfigurator configurator, Action<OptionsBuilder<StreamLifecycleOptions>> configureOptions)
            where TConfigurator : NamedServiceConfigurator, IClusterClientPersistentStreamConfigurator
        {
            configurator.Configure(configureOptions);
            return configurator;
        }

        public static TConfigurator ConfigureStreamPubSub<TConfigurator>(this TConfigurator configurator, StreamPubSubType pubsubType = StreamPubSubOptions.DEFAULT_STREAM_PUBSUB_TYPE)
            where TConfigurator : NamedServiceConfigurator, IClusterClientPersistentStreamConfigurator
        {
            configurator.Configure<TConfigurator, StreamPubSubOptions>(ob => ob.Configure(options => options.PubSubType = pubsubType));
            return configurator;
        }
    }

    public class ClusterClientPersistentStreamConfigurator : NamedServiceConfigurator, IClusterClientPersistentStreamConfigurator
    {
        public ClusterClientPersistentStreamConfigurator(string name, IClientBuilder clientBuilder, Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory)
            : base(name, configureDelegate => clientBuilder.ConfigureServices(configureDelegate))
        {
            this.ConfigureComponent(PersistentStreamProvider.Create);
            this.ConfigureComponent(PersistentStreamProvider.ParticipateIn<IClusterClientLifecycle>);
            this.ConfigureComponent(adapterFactory);
        }
    }
}
