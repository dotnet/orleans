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
    public class ClusterClientPersistentStreamConfigurator : IClusterClientPersistentStreamConfigurator
    {
        protected readonly string name;
        protected readonly IClientBuilder clientBuilder;
        public ClusterClientPersistentStreamConfigurator(string name, IClientBuilder clientBuilder)
        {
            this.name = name;
            this.clientBuilder = clientBuilder;
            //wire stream provider into lifecycle 
            this.clientBuilder.ConfigureServices(services => this.AddPersistentStream(services));
        }

        private void AddPersistentStream(IServiceCollection services)
        {
            //wire the stream provider into life cycle
            services.AddSingletonNamedService<IStreamProvider>(name, PersistentStreamProvider.Create)
                           .AddSingletonNamedService<ILifecycleParticipant<ISiloLifecycle>>(name, (s, n) => ((PersistentStreamProvider)s.GetRequiredServiceByName<IStreamProvider>(n)).ParticipateIn<ISiloLifecycle>())
                           .AddSingletonNamedService(name, (s, n) => s.GetServiceByName<IStreamProvider>(n) as IControllable)
                           .ConfigureNamedOptionForLogging<StreamInitializationOptions>(name);
        }

        public IClusterClientPersistentStreamConfigurator Configure<TOptions>(Action<OptionsBuilder<TOptions>> configureOptions) where TOptions : class, new()
        {
            clientBuilder.ConfigureServices(services =>
            {
                configureOptions?.Invoke(services.AddOptions<TOptions>(this.name));
            });
            return this;
        }
    }
    public class SiloPersistentStreamConfigurator : ISiloPersistentStreamConfigurator
    {
        protected readonly string name;
        protected readonly ISiloHostBuilder siloBuilder;
        public SiloPersistentStreamConfigurator(string name, ISiloHostBuilder siloBuilder)
        {
            this.name = name;
            this.siloBuilder = siloBuilder;
            //wire stream provider into lifecycle 
            this.siloBuilder.ConfigureServices(services => this.AddPersistentStream(services));
        }

        private void AddPersistentStream(IServiceCollection services)
        {
            //wire the stream provider into life cycle
            services.AddSingletonNamedService<IStreamProvider>(name, PersistentStreamProvider.Create)
                           .AddSingletonNamedService<ILifecycleParticipant<ISiloLifecycle>>(name, (s, n) => ((PersistentStreamProvider)s.GetRequiredServiceByName<IStreamProvider>(n)).ParticipateIn<ISiloLifecycle>())
                           .AddSingletonNamedService(name, (s, n) => s.GetServiceByName<IStreamProvider>(n) as IControllable)
                           .ConfigureNamedOptionForLogging<StreamPullingAgentOptions>(name)
                           .ConfigureNamedOptionForLogging<StreamPubSubOptions>(name)
                           .ConfigureNamedOptionForLogging<StreamInitializationOptions>(name);
        }

        public ISiloPersistentStreamConfigurator Configure<TOptions>(Action<OptionsBuilder<TOptions>> configureOptions)
            where TOptions: class, new()
        {
            siloBuilder.ConfigureServices(services =>
            {
                configureOptions?.Invoke(services.AddOptions<TOptions>(this.name));
            });
            return this;
        }

        public ISiloPersistentStreamConfigurator ConfigureComponent<TOptions, TComponent>(Action<OptionsBuilder<TOptions>> configureOptions, Func<IServiceProvider, string, TComponent> factory)
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
