using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Host;

namespace Orleans.Runtime.Startup
{
    public class SiloHostBuilder : ISiloHostBuilder
    {
        private ClusterConfiguration clusterConfiguration;
        private readonly List<Action<IServiceCollection>> configureServicesDelegates = new List<Action<IServiceCollection>>();
        private Func<IServiceCollection, IServiceProvider> serviceProviderBuilder = x => x.BuildServiceProvider();
        private Action<SiloHost> siloConfigureAction;

        public ISiloHostBuilder UseClusterConfiguration(ClusterConfiguration clusterConfiguration)
        {
            this.clusterConfiguration = clusterConfiguration;
            return this;
        }

        public ISiloHostBuilder ConfigureServices(Action<IServiceCollection> services)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            configureServicesDelegates.Add(services);
            return this;
        }

        public ISiloHostBuilder BuildServiceProvider(Func<IServiceCollection, IServiceProvider> services)
        {
            this.serviceProviderBuilder = services;
            return this;
        }

        public ISiloHostBuilder ConfigureSilo(Action<SiloHost> silo)
        {
            siloConfigureAction = silo;
            return this;
        }

        public SiloHost Build(string name)
        {
            var services = new ServiceCollection();

            StartupBuilder.RegisterSystemTypes(services);

            foreach (var configureServices in configureServicesDelegates)
            {
                configureServices(services);
            }

            var provider = serviceProviderBuilder(services);

            var host = new SiloHost(name, clusterConfiguration);
            siloConfigureAction?.Invoke(host);
            host.InitializeOrleansSilo(provider);
            return host;
        }
    }
}