using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Runtime;

namespace Orleans.Hosting
{
    /// <summary>
    /// Functionality for building <see cref="ISilo"/> instances.
    /// </summary>
    public class SiloHostBuilder : ISiloHostBuilder
    {
        private readonly ServiceProviderBuilder serviceProviderBuilder = new ServiceProviderBuilder();
        private bool built;

        /// <inheritdoc />
        public ISilo Build()
        {
            if (this.built)
                throw new InvalidOperationException($"{nameof(this.Build)} may only be called once per {nameof(SiloHostBuilder)} instance.");
            this.built = true;
            
            // Configure the container, including the default silo name & services.
            this.Configure<SiloIdentityOptions>(
                options => options.SiloName = options.SiloName ?? $"Silo_{Guid.NewGuid().ToString("N").Substring(0, 5)}");
            this.serviceProviderBuilder.ConfigureServices(
                services =>
                {
                    services.TryAddSingleton<SiloInitializationParameters>();
                    services.TryAddSingleton<Silo>(sp => new Silo(sp.GetRequiredService<SiloInitializationParameters>(), sp));
                });
            this.serviceProviderBuilder.ConfigureServices(DefaultSiloServices.AddDefaultServices);
            var serviceProvider = this.serviceProviderBuilder.BuildServiceProvider();
            
            // Construct and return the silo.
            return serviceProvider.GetRequiredService<ISilo>();
        }

        /// <inheritdoc />
        public ISiloHostBuilder ConfigureServices(Action<IServiceCollection> configureServices)
        {
            this.serviceProviderBuilder.ConfigureServices(configureServices);
            return this;
        }

        /// <inheritdoc />
        public ISiloHostBuilder UseServiceProviderFactory<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> factory)
        {
            this.serviceProviderBuilder.UseServiceProviderFactory(factory);
            return this;
        }

        /// <inheritdoc />
        public ISiloHostBuilder ConfigureContainer<TContainerBuilder>(Action<TContainerBuilder> configureContainer)
        {
            this.serviceProviderBuilder.ConfigureContainer(configureContainer);
            return this;
        }
    }
}
