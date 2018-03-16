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

namespace Orleans.Streams
{
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
