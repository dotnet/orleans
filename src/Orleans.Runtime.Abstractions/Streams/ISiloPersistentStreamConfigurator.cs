using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Streams
{
    public interface ISiloPersistentStreamConfigurator : IComponentConfigurator<ISiloPersistentStreamConfigurator> {}

    public static class SiloPersistentStreamConfiguratorExtensions
    {
        public static TConfigurator ConfigureStreamPubSub<TConfigurator>(this TConfigurator configurator, StreamPubSubType pubsubType = StreamPubSubOptions.DEFAULT_STREAM_PUBSUB_TYPE)
            where TConfigurator : ISiloPersistentStreamConfigurator
        {
            configurator.Configure<StreamPubSubOptions>(ob => ob.Configure(options => options.PubSubType = pubsubType));
            return configurator;
        }
        public static TConfigurator ConfigurePullingAgent<TConfigurator>(this TConfigurator configurator, Action<OptionsBuilder<StreamPullingAgentOptions>> configureOptions = null)
            where TConfigurator : ISiloPersistentStreamConfigurator
        {
            configurator.Configure<StreamPullingAgentOptions>(configureOptions);
            return configurator;
        }
        public static TConfigurator ConfigureLifecycle<TConfigurator>(this TConfigurator configurator, Action<OptionsBuilder<StreamLifecycleOptions>> configureOptions)
            where TConfigurator : ISiloPersistentStreamConfigurator
        {
            configurator.Configure<StreamLifecycleOptions>(configureOptions);
            return configurator;
        }

        public static TConfigurator ConfigurePartitionBalancing<TConfigurator>(this TConfigurator configurator, Func<IServiceProvider, string, IStreamQueueBalancer> factory)
            where TConfigurator : ISiloPersistentStreamConfigurator
        {
            configurator.ConfigureComponent<IStreamQueueBalancer>(factory);
            return configurator;
        }

        public static TConfigurator ConfigurePartitionBalancing<TConfigurator,TOptions>(this TConfigurator configurator,
            Func<IServiceProvider, string, IStreamQueueBalancer> factory, Action<OptionsBuilder<TOptions>> configureOptions)
            where TConfigurator : ISiloPersistentStreamConfigurator
            where TOptions : class, new()
        {
            configurator.ConfigureComponent<TOptions, IStreamQueueBalancer>(factory, configureOptions);
            return configurator;
        }

        public static TConfigurator Configure<TConfigurator, TOptions>(this TConfigurator configurator, Action<OptionsBuilder<TOptions>> configureOptions)
            where TConfigurator : ISiloPersistentStreamConfigurator
            where TOptions : class, new()
        {
            configurator.Configure<TOptions>(configureOptions);
            return configurator;
        }

        public static TConfigurator ConfigureComponent<TConfigurator, TOptions, TComponent>(this TConfigurator configurator, Func<IServiceProvider, string, TComponent> factory, Action<OptionsBuilder<TOptions>> configureOptions = null)
            where TConfigurator : ISiloPersistentStreamConfigurator
            where TOptions : class, new()
            where TComponent : class
        {
            configurator.ConfigureComponent<TOptions, TComponent>(factory, configureOptions);
            return configurator;
        }

        public static TConfigurator ConfigureComponent<TConfigurator, TComponent>(this TConfigurator configurator, Func<IServiceProvider, string, TComponent> factory)
            where TConfigurator : ISiloPersistentStreamConfigurator
            where TComponent : class
        {
            configurator.ConfigureComponent<TComponent>(factory);
            return configurator;
        }
    }
}
