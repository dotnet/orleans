using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Streams;

namespace Orleans.Hosting
{
    public class PersistentStreamStorageConfigurationValidator : IConfigurationValidator
    {
        private IServiceProvider services;
        private const string pubsubStoreName = "PubSubStore";
        private string streamProviderName;

        private PersistentStreamStorageConfigurationValidator(IServiceProvider services, string streamProviderName)
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

        public static IConfigurationValidator Create(IServiceProvider services, string name)
        {
            return new PersistentStreamStorageConfigurationValidator(services, name);
        }
    }

    public class SiloPersistentStreamConfigurator : NamedServiceConfigurator, ISiloPersistentStreamConfigurator
    {
        public SiloPersistentStreamConfigurator(string name, Action<Action<IServiceCollection>> configureDelegate, Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory)
            : base(name, configureDelegate)
        {
            this.ConfigureDelegate(services => services.AddSiloStreaming());
            this.ConfigureComponent(PersistentStreamProvider.Create);
            this.ConfigureComponent((s, n) => s.GetServiceByName<IStreamProvider>(n) as IControllable);
            this.ConfigureComponent(PersistentStreamProvider.ParticipateIn<ISiloLifecycle>);
            this.ConfigureComponent(adapterFactory);
            this.ConfigureComponent(PersistentStreamStorageConfigurationValidator.Create);
        }
    }
}
