using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Providers.Streams.Common;

namespace Orleans.Streams
{
    public interface IClusterClientPersistentStreamConfigurator : IComponentConfigurator<IClusterClientPersistentStreamConfigurator> { }

    public static class ClusterClientPersistentStreamConfiguratorExtensions
    {
        public static IClusterClientPersistentStreamConfigurator ConfigureLifecycle(this IClusterClientPersistentStreamConfigurator configurator, Action<OptionsBuilder<StreamLifecycleOptions>> configureOptions)
        {
            configurator.Configure<StreamLifecycleOptions>(configureOptions);
            return configurator;
        }

        public static IClusterClientPersistentStreamConfigurator ConfigureStreamPubSub(this IClusterClientPersistentStreamConfigurator configurator, StreamPubSubType pubsubType = StreamPubSubOptions.DEFAULT_STREAM_PUBSUB_TYPE)
        {
            configurator.Configure<StreamPubSubOptions>(ob => ob.Configure(options => options.PubSubType = pubsubType));
            return configurator;
        }
    }

    public class ClusterClientPersistentStreamConfigurator : NamedServiceConfigurator<IClusterClientPersistentStreamConfigurator>, IClusterClientPersistentStreamConfigurator
    {
        private Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory;
        public ClusterClientPersistentStreamConfigurator(string name, IClientBuilder clientBuilder, Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory)
            : base(name, configureDelegate => clientBuilder.ConfigureServices(configureDelegate))
        {
            this.adapterFactory = adapterFactory;
            //wire stream provider into lifecycle 
            this.configureDelegate(services => this.AddPersistentStream(services));
        }

        private void AddPersistentStream(IServiceCollection services)
        {
            //wire the stream provider into life cycle
            services.AddSingletonNamedService<IStreamProvider>(name, PersistentStreamProvider.Create)
                           .AddSingletonNamedService<ILifecycleParticipant<IClusterClientLifecycle>>(name, 
                           (s, n) => ((PersistentStreamProvider)s.GetRequiredServiceByName<IStreamProvider>(n)).ParticipateIn<IClusterClientLifecycle>())
                           .AddSingletonNamedService<IQueueAdapterFactory>(name, adapterFactory)
                           .ConfigureNamedOptionForLogging<StreamLifecycleOptions>(name)
                           .ConfigureNamedOptionForLogging<StreamPubSubOptions>(name);
        }
    }
}
