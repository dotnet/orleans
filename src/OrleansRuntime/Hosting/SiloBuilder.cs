using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Orleans.Runtime.Hosting
{
    /// <summary>
    /// Functionality for building <see cref="ISilo"/> instances.
    /// </summary>
    public class SiloBuilder : ISiloBuilder
    {
        private readonly List<Action<IServiceCollection>> configureServicesDelegates = new List<Action<IServiceCollection>>();
        private readonly List<object> configureContainerDelegates = new List<object>();
        private IServiceProviderFactory serviceProviderFactory;
        private bool built;

        /// <inheritdoc />
        public ISilo Build()
        {
            if (this.built)
                throw new InvalidOperationException($"{nameof(this.Build)} may only be called once per {nameof(SiloBuilder)} instance.");
            this.built = true;
            
            // Configure the container.
            var services = new ServiceCollection();
            foreach (var configureServices in this.configureServicesDelegates)
            {
                configureServices(services);
            }

            // Configure a default silo name and add the default services.
            this.Configure<SiloIdentityOptions>(
                options => options.SiloName = options.SiloName ?? $"Silo_{Guid.NewGuid().ToString("N").Substring(0, 5)}");
            services.TryAddSingleton<SiloInitializationParameters>();
            services.TryAddSingleton<Silo>(sp => new Silo(sp.GetRequiredService<SiloInitializationParameters>(), sp));
            DefaultSiloServices.AddDefaultServices(services);

            // If no service provider factory has been specified, set a default.
            if (this.serviceProviderFactory == null)
            {
                var factory = new DelegateServiceProviderFactory(svc => svc.BuildServiceProvider());
                this.UseServiceProviderFactory(factory);
            }

            // Create the service provider using the configured factory.
            var serviceProvider = this.serviceProviderFactory.BuildServiceProvider(services);
            
            // Construct and return the silo.
            var result = serviceProvider.GetRequiredService<ISilo>();
            return result;
        }

        /// <inheritdoc />
        public ISiloBuilder ConfigureServices(Action<IServiceCollection> configureServices)
        {
            if (configureServices == null) throw new ArgumentNullException(nameof(configureServices));
            this.configureServicesDelegates.Add(configureServices);
            return this;
        }

        /// <inheritdoc />
        public ISiloBuilder UseServiceProviderFactory<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> factory)
        {
            if (this.serviceProviderFactory != null)
                throw new InvalidOperationException("The service provider factory has already been specified.");
            this.serviceProviderFactory = new ServiceProviderFactoryAdapter<TContainerBuilder>(factory);
            foreach (var builder in this.configureContainerDelegates)
            {
                var typedDelegate = (Action<TContainerBuilder>) builder;
                this.serviceProviderFactory.ConfigureContainer(typedDelegate);
            }

            this.configureContainerDelegates.Clear();
            return this;
        }

        /// <inheritdoc />
        public ISiloBuilder ConfigureContainer<TContainerBuilder>(Action<TContainerBuilder> configureContainer)
        {
            if (this.serviceProviderFactory != null) this.serviceProviderFactory.ConfigureContainer(configureContainer);
            else this.configureContainerDelegates.Add(configureContainer);

            return this;
        }
    }
}
