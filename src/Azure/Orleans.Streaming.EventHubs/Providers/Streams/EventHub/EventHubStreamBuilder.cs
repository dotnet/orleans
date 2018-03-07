using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.ServiceBus.Providers;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    public class SiloEventHubStreamConfigurator : SiloRecoverableStreamConfigurator
    {
        public SiloEventHubStreamConfigurator(string name, ISiloHostBuilder builder)
            : base(name, builder)
        {
            this.siloBuilder.ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(EventHubAdapterFactory).Assembly))
                .AddPersistentStreams(this.name, EventHubAdapterFactory.Create)
                .ConfigureServices(services => services.ConfigureNamedOptionForLogging<EventHubOptions>(name)
                    .ConfigureNamedOptionForLogging<EventHubReceiverOptions>(name)
                    .ConfigureNamedOptionForLogging<EventHubStreamCacheOptions>(name));
        }

        public SiloEventHubStreamConfigurator ConfigureCheckpointer<TOptions>(Action<OptionsBuilder<TOptions>> configureOptions, Func<IServiceProvider, string, IStreamQueueCheckpointerFactory> checkpointerFactoryBuilder)
            where TOptions : class, new()
        {
            this.ConfigureComponent<TOptions, IStreamQueueCheckpointerFactory>(configureOptions, checkpointerFactoryBuilder);
            return this;
        }

        public SiloEventHubStreamConfigurator ConfigureEventHub(Action<OptionsBuilder<EventHubOptions>> configureOptions)
        {
            this.Configure<EventHubOptions>(configureOptions);
            return this;
        }

        public SiloEventHubStreamConfigurator ConfigureQueueReceiver(Action<OptionsBuilder<EventHubReceiverOptions>> configureOptions)
        {
            this.Configure<EventHubReceiverOptions>(configureOptions);
            return this;
        }

        public SiloEventHubStreamConfigurator ConfigureCache(Action<OptionsBuilder<EventHubStreamCacheOptions>> configureOptions)
        {
            this.Configure<EventHubStreamCacheOptions>(configureOptions);
            return this;
        }
    }

    public class ClusterClientEventHubStreamConfigurator : ClusterClientPersistentStreamConfigurator
    {
        public ClusterClientEventHubStreamConfigurator(string name, IClientBuilder builder)
           : base(name, builder)
        {
            this.clientBuilder.ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(EventHubAdapterFactory).Assembly))
                .AddPersistentStreams(this.name, EventHubAdapterFactory.Create)
                .ConfigureServices(services => services.ConfigureNamedOptionForLogging<EventHubOptions>(name));
        }

        public ClusterClientEventHubStreamConfigurator ConfigureEventHub(Action<OptionsBuilder<EventHubOptions>> configureOptions)
        {
            this.Configure<EventHubOptions>(configureOptions);
            return this;
        }
    }
}
