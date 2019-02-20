using Orleans.Configuration;
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Options;

namespace Orleans.Streams
{
    public interface ISiloPersistentStreamConfigurator
    {
        ISiloPersistentStreamConfigurator Configure<TOptions>(Action<OptionsBuilder<TOptions>> configureOptions)
          where TOptions : class, new();

        ISiloPersistentStreamConfigurator ConfigureComponent<TOptions, TComponent>(Func<IServiceProvider, string, TComponent> factory, Action<OptionsBuilder<TOptions>> configureOptions = null)
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
        public static ISiloPersistentStreamConfigurator ConfigurePullingAgent(this ISiloPersistentStreamConfigurator configurator, Action<OptionsBuilder<StreamPullingAgentOptions>> configureOptions = null)
        {
            configurator.Configure<StreamPullingAgentOptions>(configureOptions);
            return configurator;
        }
        public static ISiloPersistentStreamConfigurator ConfigureLifecycle(this ISiloPersistentStreamConfigurator configurator, Action<OptionsBuilder<StreamLifecycleOptions>> configureOptions)
        {
            configurator.Configure<StreamLifecycleOptions>(configureOptions);
            return configurator;
        }

        public static ISiloPersistentStreamConfigurator ConfigurePartitionBalancing(this ISiloPersistentStreamConfigurator configurator, Func<IServiceProvider, string, IStreamQueueBalancer> factory)
        {
            return configurator.ConfigureComponent<IStreamQueueBalancer>(factory);
        }

        public static ISiloPersistentStreamConfigurator ConfigurePartitionBalancing<TOptions>(this ISiloPersistentStreamConfigurator configurator,
            Func<IServiceProvider, string, IStreamQueueBalancer> factory, Action<OptionsBuilder<TOptions>> configureOptions)
            where TOptions : class, new()
        {
            return configurator.ConfigureComponent<TOptions, IStreamQueueBalancer>(factory, configureOptions);
        }
    }
}
