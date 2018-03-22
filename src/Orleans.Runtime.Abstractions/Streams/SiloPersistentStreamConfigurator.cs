using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Text;
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

    public class SiloPersistentStreamConfigurator : ISiloPersistentStreamConfigurator
    {
        protected readonly string name;
        protected readonly ISiloHostBuilder siloBuilder;
        private Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory;
        public SiloPersistentStreamConfigurator(string name, ISiloHostBuilder siloBuilder, Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory)
        {
            this.name = name;
            this.siloBuilder = siloBuilder;
            this.adapterFactory = adapterFactory;
            //wire stream provider into lifecycle 
            this.siloBuilder.ConfigureServices(services => this.AddPersistentStream(services));
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

        public ISiloPersistentStreamConfigurator Configure<TOptions>(Action<OptionsBuilder<TOptions>> configureOptions)
            where TOptions: class, new()
        {
            siloBuilder.ConfigureServices(services =>
            {
                configureOptions?.Invoke(services.AddOptions<TOptions>(this.name));
                services.ConfigureNamedOptionForLogging<TOptions>(this.name);
            });
            return this;
        }

        public ISiloPersistentStreamConfigurator ConfigureComponent<TOptions, TComponent>(Func<IServiceProvider, string, TComponent> factory, Action<OptionsBuilder<TOptions>> configureOptions = null)
            where TOptions : class, new()
            where TComponent: class
        {
            this.Configure<TOptions>(configureOptions);
            this.ConfigureComponent<TComponent>(factory);
            return this;
        }

        public ISiloPersistentStreamConfigurator ConfigureComponent<TComponent>(Func<IServiceProvider, string, TComponent> factory)
           where TComponent : class
        {
            siloBuilder.ConfigureServices(services =>
            {
                services.AddSingletonNamedService<TComponent>(name, factory);
            });
            return this;
        }
        //try configure defaults if required is not configured
        public virtual void TryConfigureDefaults()
        { }

    }
}
