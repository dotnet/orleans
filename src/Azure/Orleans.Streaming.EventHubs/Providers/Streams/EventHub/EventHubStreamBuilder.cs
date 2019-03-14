using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.ServiceBus.Providers;
using System;
using Microsoft.Extensions.Options;
using Orleans.Providers.Streams.Common;
using Orleans.ApplicationParts;

namespace Orleans.Streams
{
    public class SiloEventHubStreamConfigurator : SiloRecoverableStreamConfigurator
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
            this.configureDelegate(services => services.ConfigureNamedOptionForLogging<EventHubOptions>(name)
                .ConfigureNamedOptionForLogging<EventHubReceiverOptions>(name)
                .ConfigureNamedOptionForLogging<EventHubStreamCachePressureOptions>(name)
                .AddTransient<IConfigurationValidator>(sp => new EventHubOptionsValidator(sp.GetOptionsByName<EventHubOptions>(name), name))
                .AddTransient<IConfigurationValidator>(sp => new StreamCheckpointerConfigurationValidator(sp, name)));
        }

        public SiloEventHubStreamConfigurator ConfigureCheckpointer<TOptions>(Func<IServiceProvider, string, IStreamQueueCheckpointerFactory> checkpointerFactoryBuilder, Action<OptionsBuilder<TOptions>> configureOptions)
            where TOptions : class, new()
        {
            this.ConfigureComponent<TOptions, IStreamQueueCheckpointerFactory>(checkpointerFactoryBuilder, configureOptions);
            return this;
        }

        public SiloEventHubStreamConfigurator ConfigureEventHub(Action<OptionsBuilder<EventHubOptions>> configureOptions)
        {
            this.Configure<EventHubOptions>(configureOptions);
            return this;
        }

        public SiloEventHubStreamConfigurator ConfigurePartitionReceiver(Action<OptionsBuilder<EventHubReceiverOptions>> configureOptions)
        {
            this.Configure<EventHubReceiverOptions>(configureOptions);
            return this;
        }

        public SiloEventHubStreamConfigurator ConfigureCachePressuring(Action<OptionsBuilder<EventHubStreamCachePressureOptions>> configureOptions)
        {
            this.Configure<EventHubStreamCachePressureOptions>(configureOptions);
            return this;
        }
    }

    public class ClusterClientEventHubStreamConfigurator : ClusterClientPersistentStreamConfigurator
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

        public ClusterClientEventHubStreamConfigurator ConfigureEventHub(Action<OptionsBuilder<EventHubOptions>> configureOptions)
        {
            this.Configure<EventHubOptions>(configureOptions);
            return this;
        }
    }
}
