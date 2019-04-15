using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Streams
{
    public interface ISiloPersistentStreamConfigurator { }

    public static class SiloPersistentStreamConfiguratorExtensions
    {
        public static TConfigurator ConfigureStreamPubSub<TConfigurator>(this TConfigurator configurator, StreamPubSubType pubsubType = StreamPubSubOptions.DEFAULT_STREAM_PUBSUB_TYPE)
            where TConfigurator : NamedServiceConfigurator, ISiloPersistentStreamConfigurator
        {
            configurator.Configure<TConfigurator, StreamPubSubOptions>(ob => ob.Configure(options => options.PubSubType = pubsubType));
            return configurator;
        }

        public static TConfigurator ConfigurePullingAgent<TConfigurator>(this TConfigurator configurator, Action<OptionsBuilder<StreamPullingAgentOptions>> configureOptions = null)
            where TConfigurator : NamedServiceConfigurator, ISiloPersistentStreamConfigurator
        {
            configurator.Configure(configureOptions);
            return configurator;
        }

        public static TConfigurator ConfigureLifecycle<TConfigurator>(this TConfigurator configurator, Action<OptionsBuilder<StreamLifecycleOptions>> configureOptions)
            where TConfigurator : NamedServiceConfigurator, ISiloPersistentStreamConfigurator
        {
            configurator.Configure(configureOptions);
            return configurator;
        }

        public static TConfigurator ConfigurePartitionBalancing<TConfigurator>(this TConfigurator configurator, Func<IServiceProvider, string, IStreamQueueBalancer> factory)
            where TConfigurator : NamedServiceConfigurator, ISiloPersistentStreamConfigurator
        {
            configurator.ConfigureComponent(factory);
            return configurator;
        }

        public static TConfigurator ConfigurePartitionBalancing<TConfigurator,TOptions>(this TConfigurator configurator,
            Func<IServiceProvider, string, IStreamQueueBalancer> factory, Action<OptionsBuilder<TOptions>> configureOptions)
            where TConfigurator : NamedServiceConfigurator, ISiloPersistentStreamConfigurator
            where TOptions : class, new()
        {
            configurator.ConfigureComponent(factory, configureOptions);
            return configurator;
        }
    }
}
