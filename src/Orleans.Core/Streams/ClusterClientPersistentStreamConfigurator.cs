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
        public static TConfigurator ConfigureLifecycle<TConfigurator>(this TConfigurator configurator, Action<OptionsBuilder<StreamLifecycleOptions>> configureOptions)
            where TConfigurator : IClusterClientPersistentStreamConfigurator
        {
            configurator.Configure<StreamLifecycleOptions>(configureOptions);
            return configurator;
        }

        public static TConfigurator ConfigureStreamPubSub<TConfigurator>(this TConfigurator configurator, StreamPubSubType pubsubType = StreamPubSubOptions.DEFAULT_STREAM_PUBSUB_TYPE)
            where TConfigurator : IClusterClientPersistentStreamConfigurator
        {
            configurator.Configure<StreamPubSubOptions>(ob => ob.Configure(options => options.PubSubType = pubsubType));
            return configurator;
        }

        public static TConfigurator Configure<TConfigurator, TOptions>(this TConfigurator configurator, Action<OptionsBuilder<TOptions>> configureOptions)
            where TConfigurator : IClusterClientPersistentStreamConfigurator
            where TOptions : class, new()
        {
            configurator.Configure<TOptions>(configureOptions);
            return configurator;
        }

        public static TConfigurator ConfigureComponent<TConfigurator, TOptions, TComponent>(this TConfigurator configurator, Func<IServiceProvider, string, TComponent> factory, Action<OptionsBuilder<TOptions>> configureOptions = null)
            where TConfigurator : IClusterClientPersistentStreamConfigurator
            where TOptions : class, new()
            where TComponent : class
        {
            configurator.ConfigureComponent<TOptions, TComponent>(factory, configureOptions);
            return configurator;
        }

        public static TConfigurator ConfigureComponent<TConfigurator, TComponent>(this TConfigurator configurator, Func<IServiceProvider, string, TComponent> factory)
            where TConfigurator : IClusterClientPersistentStreamConfigurator
            where TComponent : class
        {
            configurator.ConfigureComponent<TComponent>(factory);
            return configurator;
        }
    }

    public class ClusterClientPersistentStreamConfigurator : NamedServiceConfigurator<IClusterClientPersistentStreamConfigurator>, IClusterClientPersistentStreamConfigurator
    {
        public ClusterClientPersistentStreamConfigurator(string name, IClientBuilder clientBuilder, Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory)
            : base(name, configureDelegate => clientBuilder.ConfigureServices(configureDelegate))
        {
            ConfigureComponent<IStreamProvider>(PersistentStreamProvider.Create);
            ConfigureComponent<ILifecycleParticipant<IClusterClientLifecycle>>(PersistentStreamProvider.ParticipateIn<IClusterClientLifecycle>);
            ConfigureComponent<IQueueAdapterFactory>(adapterFactory);
        }
    }
}
