using System;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.ServiceBus.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.ApplicationParts;

namespace Orleans.Streams
{
    public interface IEventHubStreamConfigurator {}

    public static class EventHubStreamConfiguratorExtensions
    {
        public static TConfigurator ConfigureEventHub<TConfigurator>(this TConfigurator configurator, Action<OptionsBuilder<EventHubOptions>> configureOptions)
            where TConfigurator : NamedServiceConfigurator, IEventHubStreamConfigurator
        {
            configurator.Configure(configureOptions);
            return configurator;
        }
    }

    public interface ISiloEventHubStreamConfigurator : IEventHubStreamConfigurator, ISiloRecoverableStreamConfigurator { }


    public static class SiloEventHubStreamConfiguratorExtensions
    {
        public static TConfigurator ConfigureCheckpointer<TConfigurator,TOptions>(this TConfigurator configurator, Func<IServiceProvider, string, IStreamQueueCheckpointerFactory> checkpointerFactoryBuilder, Action<OptionsBuilder<TOptions>> configureOptions)
            where TConfigurator : NamedServiceConfigurator, ISiloEventHubStreamConfigurator
            where TOptions : class, new()
        {
            configurator.ConfigureComponent(checkpointerFactoryBuilder, configureOptions);
            return configurator;
        }

        public static TConfigurator ConfigurePartitionReceiver<TConfigurator>(this TConfigurator configurator, Action<OptionsBuilder<EventHubReceiverOptions>> configureOptions)
            where TConfigurator : NamedServiceConfigurator, ISiloEventHubStreamConfigurator
        {
            configurator.Configure(configureOptions);
            return configurator;
        }

        public static TConfigurator ConfigureCachePressuring<TConfigurator>(this TConfigurator configurator, Action<OptionsBuilder<EventHubStreamCachePressureOptions>> configureOptions)
            where TConfigurator : NamedServiceConfigurator, ISiloEventHubStreamConfigurator
        {
            configurator.Configure(configureOptions);
            return configurator;
        }

        public static TConfigurator UseAzureTableCheckpointer<TConfigurator>(this TConfigurator configurator, Action<OptionsBuilder<AzureTableStreamCheckpointerOptions>> configureOptions)
            where TConfigurator : NamedServiceConfigurator, ISiloEventHubStreamConfigurator
        {
            configurator.ConfigureCheckpointer(EventHubCheckpointerFactory.CreateFactory, configureOptions);
            return configurator;
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
