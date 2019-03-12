using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Streams
{
    public class PersistentStreamStorageConfigurationValidator : IConfigurationValidator
    {
        private IServiceProvider services;
        private const string pubsubStoreName = "PubSubStore";
        private string streamProviderName;
        public PersistentStreamStorageConfigurationValidator(IServiceProvider services, string streamProviderName)
        {
            this.services = services;
            this.streamProviderName = streamProviderName;
        }

        public void ValidateConfiguration()
        {
            var pubsubOptions = services.GetOptionsByName<StreamPubSubOptions>(this.streamProviderName);
            if (pubsubOptions.PubSubType == StreamPubSubType.ExplicitGrainBasedAndImplicit || pubsubOptions.PubSubType == StreamPubSubType.ExplicitGrainBasedOnly)
            {
                var pubsubStore = services.GetServiceByName<IGrainStorage>(pubsubStoreName);
                if (pubsubStore == null)
                    throw new OrleansConfigurationException(
                        $" Streams with pubsub type {StreamPubSubType.ExplicitGrainBasedAndImplicit} and {StreamPubSubType.ExplicitGrainBasedOnly} requires a grain storage named {pubsubStoreName} " +
                        $"to be configured with silo. Please configure one for your stream {streamProviderName}.");
            }
        }
    }

    public class SiloPersistentStreamConfigurator : NamedServiceConfigurator<ISiloPersistentStreamConfigurator>, ISiloPersistentStreamConfigurator
    {
        private Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory;
        public SiloPersistentStreamConfigurator(string name, Action<Action<IServiceCollection>> configureDelegate, Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory)
            : base(name, configureDelegate)
        {
            this.adapterFactory = adapterFactory;
            //wire stream provider into lifecycle
            this.configureDelegate(services => this.AddPersistentStream(services));
        }

        private void AddPersistentStream(IServiceCollection services)
        {
            //wire the stream provider into life cycle
            services.AddSingletonNamedService<IStreamProvider>(name, PersistentStreamProvider.Create)
                           .AddSingletonNamedService<ILifecycleParticipant<ISiloLifecycle>>(name, (s, n) => ((PersistentStreamProvider)s.GetRequiredServiceByName<IStreamProvider>(n)).ParticipateIn<ISiloLifecycle>())
                           .AddSingletonNamedService<IQueueAdapterFactory>(name, adapterFactory)
                           .AddSingletonNamedService(name, (s, n) => s.GetServiceByName<IStreamProvider>(n) as IControllable)
                           .AddSingleton<IConfigurationValidator>(sp => new PersistentStreamStorageConfigurationValidator(sp, name))
                           .ConfigureNamedOptionForLogging<StreamPullingAgentOptions>(name)
                           .ConfigureNamedOptionForLogging<StreamPubSubOptions>(name)
                           .ConfigureNamedOptionForLogging<StreamLifecycleOptions>(name);
        }

        //try configure defaults if required is not configured
        public virtual void TryConfigureDefaults()
        { }
    }
}
