using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Streams;

namespace Orleans.Hosting
{
    public interface ISiloPersistentStreamConfigurator : IPersistentStreamConfigurator { }

    public static class SiloPersistentStreamConfiguratorExtensions
    {
        public static void ConfigurePullingAgent(this ISiloPersistentStreamConfigurator configurator, Action<OptionsBuilder<StreamPullingAgentOptions>> configureOptions = null)
        {
            configurator.Configure(configureOptions);
        }

        public static void ConfigureLifecycle(this ISiloPersistentStreamConfigurator configurator, Action<OptionsBuilder<StreamLifecycleOptions>> configureOptions)
        {
            configurator.Configure(configureOptions);
        }

        public static void ConfigurePartitionBalancing(this ISiloPersistentStreamConfigurator configurator, Func<IServiceProvider, string, IStreamQueueBalancer> factory)
        {
            configurator.ConfigureComponent(factory);
        }

        public static void ConfigurePartitionBalancing<TOptions>(this ISiloPersistentStreamConfigurator configurator,
            Func<IServiceProvider, string, IStreamQueueBalancer> factory, Action<OptionsBuilder<TOptions>> configureOptions)
            where TOptions : class, new()
        {
            configurator.ConfigureComponent(factory, configureOptions);
        }
    }
}
