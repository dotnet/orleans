using System;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.ServiceBus.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.ApplicationParts;
using Orleans.Streams;

namespace Orleans.Hosting
{
    public interface IEventHubStreamConfigurator : INamedServiceConfigurator {}

    public static class EventHubStreamConfiguratorExtensions
    {
        public static void ConfigureEventHub(this IEventHubStreamConfigurator configurator, Action<OptionsBuilder<EventHubOptions>> configureOptions)
        {
            configurator.Configure(configureOptions);
        }

        public static void UseDataAdapter(this IEventHubStreamConfigurator configurator, Func<IServiceProvider, string, IEventHubDataAdapter> factory)
        {
            configurator.ConfigureComponent(factory);
        }
    }

    public interface ISiloEventHubStreamConfigurator : IEventHubStreamConfigurator, ISiloRecoverableStreamConfigurator { }


    public static class SiloEventHubStreamConfiguratorExtensions
    {
        public static void ConfigureCheckpointer<TOptions>(this ISiloEventHubStreamConfigurator configurator, Func<IServiceProvider, string, IStreamQueueCheckpointerFactory> checkpointerFactoryBuilder, Action<OptionsBuilder<TOptions>> configureOptions)
            where TOptions : class, new()
        {
            configurator.ConfigureComponent(checkpointerFactoryBuilder, configureOptions);
        }

        public static void ConfigurePartitionReceiver(this ISiloEventHubStreamConfigurator configurator, Action<OptionsBuilder<EventHubReceiverOptions>> configureOptions)
        {
            configurator.Configure(configureOptions);
        }

        public static void ConfigureCachePressuring(this ISiloEventHubStreamConfigurator configurator, Action<OptionsBuilder<EventHubStreamCachePressureOptions>> configureOptions)
        {
            configurator.Configure(configureOptions);
        }

        public static void UseAzureTableCheckpointer(this ISiloEventHubStreamConfigurator configurator, Action<OptionsBuilder<AzureTableStreamCheckpointerOptions>> configureOptions)
        {
            configurator.ConfigureCheckpointer(EventHubCheckpointerFactory.CreateFactory, configureOptions);
        }
    }

    public class SiloEventHubStreamConfigurator : SiloRecoverableStreamConfigurator, ISiloEventHubStreamConfigurator
    {
        public SiloEventHubStreamConfigurator(string name,
            Action<Action<IServiceCollection>> configureServicesDelegate, Action<Action<IApplicationPartManager>> configureAppPartsDelegate)
            : base(name, configureServicesDelegate, EventHubAdapterFactory.Create)
        {
            configureAppPartsDelegate(parts =>
                {
                    parts.AddFrameworkPart(typeof(EventHubAdapterFactory).Assembly)
                        .AddFrameworkPart(typeof(EventSequenceTokenV2).Assembly);
                });
            this.ConfigureDelegate(services => services.ConfigureNamedOptionForLogging<EventHubOptions>(name)
                .ConfigureNamedOptionForLogging<EventHubReceiverOptions>(name)
                .ConfigureNamedOptionForLogging<EventHubStreamCachePressureOptions>(name)
                .AddTransient<IConfigurationValidator>(sp => new EventHubOptionsValidator(sp.GetOptionsByName<EventHubOptions>(name), name))
                .AddTransient<IConfigurationValidator>(sp => new StreamCheckpointerConfigurationValidator(sp, name)));
        }
    }

    public interface IClusterClientEventHubStreamConfigurator : IEventHubStreamConfigurator, IClusterClientPersistentStreamConfigurator { }

    public class ClusterClientEventHubStreamConfigurator : ClusterClientPersistentStreamConfigurator, IClusterClientEventHubStreamConfigurator
    {
        public ClusterClientEventHubStreamConfigurator(string name, IClientBuilder builder)
           : base(name, builder, EventHubAdapterFactory.Create)
        {
            builder.ConfigureApplicationParts(parts =>
                {
                    parts.AddFrameworkPart(typeof(EventHubAdapterFactory).Assembly)
                        .AddFrameworkPart(typeof(EventSequenceTokenV2).Assembly);
                })
                .ConfigureServices(services => services.ConfigureNamedOptionForLogging<EventHubOptions>(name)
                .AddTransient<IConfigurationValidator>(sp => new EventHubOptionsValidator(sp.GetOptionsByName<EventHubOptions>(name), name)));
        }
    }
}
