using Orleans.Configuration;
using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Streams
{
    public interface ISiloPersistentStreamConfigurator
    {
        ISiloPersistentStreamConfigurator Configure<TOptions>(Action<OptionsBuilder<TOptions>> configureOptions)
          where TOptions : class, new();

        ISiloPersistentStreamConfigurator ConfigureComponent<TOptions, TComponent>(Action<OptionsBuilder<TOptions>> configureOptions, Func<IServiceProvider, string, TComponent> factory)
            where TOptions : class, new()
            where TComponent : class;

        ISiloPersistentStreamConfigurator ConfigureComponent<TComponent>(Func<IServiceProvider, string, TComponent> factory)
            where TComponent : class;
    }

    public static class SiloPersistentStreamConfiguratorExtensions
    {
        public static ISiloPersistentStreamConfigurator ConfigureStreamPubSub(this ISiloPersistentStreamConfigurator configurator, StreamPubSubType pubsubType = StreamPubSubOptions.DEFAULT_STREAM_PUBSUB_TYPE)
        {
            configurator.Configure<StreamPubSubOptions>(ob => ob.Configure(options => options.PubSubType = pubsubType));
            return configurator;
        }
        public static ISiloPersistentStreamConfigurator ConfigurePullingAgent(this ISiloPersistentStreamConfigurator configurator, Action<OptionsBuilder<StreamPullingAgentOptions>> configureOptions)
        {
            configurator.Configure<StreamPullingAgentOptions>(configureOptions);
            return configurator;
        }
        public static ISiloPersistentStreamConfigurator ConfigureInitialization(this ISiloPersistentStreamConfigurator configurator, Action<OptionsBuilder<StreamInitializationOptions>> configureOptions)
        {
            configurator.Configure<StreamInitializationOptions>(configureOptions);
            return configurator;
        }

        public static ISiloPersistentStreamConfigurator ConfigureStreamQueueBalancer(this ISiloPersistentStreamConfigurator configurator, Func<IServiceProvider, string, IStreamQueueBalancer> factory)
        {
            return configurator.ConfigureComponent<IStreamQueueBalancer>(factory);
        }

        public static ISiloPersistentStreamConfigurator ConfigureStreamQueueBalancer<TOptions>(this ISiloPersistentStreamConfigurator configurator,
            Action<OptionsBuilder<TOptions>> configureOptions, Func<IServiceProvider, string, IStreamQueueBalancer> factory)
            where TOptions : class, new()
        {
            return configurator.ConfigureComponent<TOptions, IStreamQueueBalancer>(configureOptions, factory);
        }
    }

    public interface IClusterClientPersistentStreamConfigurator
    {
        IClusterClientPersistentStreamConfigurator Configure<TOptions>(Action<OptionsBuilder<TOptions>> configureOptions)
        where TOptions : class, new();
    }

    public static class ClusterClientPersistentStreamConfiguratorExtensions
    {
        public static IClusterClientPersistentStreamConfigurator ConfigureInitialization(this IClusterClientPersistentStreamConfigurator configurator, Action<OptionsBuilder<StreamInitializationOptions>> configureOptions)
        {
            configurator.Configure<StreamInitializationOptions>(configureOptions);
            return configurator;
        }
    }
}
