using System;
using System.Collections.Generic;
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
        private Func<IServiceCollection, IServiceProvider> containerBuilder;
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

            // Configure a default silo name.
            this.Configure<SiloIdentityOptions>(
                options => options.SiloName = options.SiloName ?? $"Silo_{Guid.NewGuid().ToString("N").Substring(0, 5)}");
            services.TryAddSingleton<SiloInitializationParameters>();
            services.TryAddSingleton<Silo>(sp => new Silo(sp.GetRequiredService<SiloInitializationParameters>(), sp));
            DefaultSiloServices.AddDefaultServices(services);
            
            if (this.containerBuilder == null) this.containerBuilder = svc => svc.BuildServiceProvider();
            var serviceProvider = this.containerBuilder(services);
            
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
        public ISiloBuilder UseServiceProviderFactory(Func<IServiceCollection, IServiceProvider> configureServiceProvider)
        {
            if (configureServiceProvider == null) throw new ArgumentNullException(nameof(configureServiceProvider));
            if (this.containerBuilder != null) throw new InvalidOperationException($"The {nameof(this.UseServiceProviderFactory)} delegate has already been specified.");
            this.containerBuilder = configureServiceProvider;
            return this;
        }
    }
}
