using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Streams
{
    public interface ISiloPersistentStreamConfigurator : IComponentConfigurator<ISiloPersistentStreamConfigurator> {}

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
