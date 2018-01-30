using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;
using Orleans.ApplicationParts;
using Orleans.Serialization;

namespace Orleans
{
    /// <summary>
    /// Builder used for creating <see cref="IClusterClient"/> instances.
    /// </summary>
    public class ClientBuilder : IClientBuilder
    {
        private readonly ServiceProviderBuilder serviceProviderBuilder = new ServiceProviderBuilder();
        private bool built;
        
        /// <inheritdoc />
        public IDictionary<object, object> Properties { get; } = new Dictionary<object, object>();

        /// <summary>
        /// Returns a new default client builder.
        /// </summary>
        /// <returns>A new default client builder.</returns>
        public static IClientBuilder CreateDefault()
            => new ClientBuilder()
                .ConfigureDefaults()
                .ConfigureApplicationParts(
                    parts => parts
                        .AddFromApplicationBaseDirectory()
                        .AddFromAppDomain());

        /// <inheritdoc />
        public IClusterClient Build()
        {
            if (this.built) throw new InvalidOperationException($"{nameof(this.Build)} may only be called once per {nameof(ClientBuilder)} instance.");
            this.built = true;

            // Configure default services and build the container.
            this.ConfigureServices(EnsureClientConfigurationConfigured);
            this.ConfigureDefaults();

            var serviceProvider = this.serviceProviderBuilder.BuildServiceProvider(new HostBuilderContext(this.Properties));
            ValidateSystemConfiguration(serviceProvider);

            // Construct and return the cluster client.
            serviceProvider.GetService<SerializationManager>().RegisterSerializers(serviceProvider.GetService<IApplicationPartManager>());
            serviceProvider.GetRequiredService<OutsideRuntimeClient>().ConsumeServices(serviceProvider);
            return serviceProvider.GetRequiredService<IClusterClient>();
        }

        /// <inheritdoc />
        public IClientBuilder UseConfiguration(ClientConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            this.serviceProviderBuilder.ConfigureServices((context, services) => services.AddLegacyClientConfigurationSupport(configuration));
            return this;
        }

        /// <inheritdoc />
        public IClientBuilder ConfigureServices(Action<IServiceCollection> configureServices)
        {
            if (configureServices == null) throw new ArgumentNullException(nameof(configureServices));
            this.serviceProviderBuilder.ConfigureServices((context, services) => configureServices(services));
            return this;
        }

        /// <inheritdoc />
        public IClientBuilder UseServiceProviderFactory<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> factory)
        {
            this.serviceProviderBuilder.UseServiceProviderFactory(factory);
            return this;
        }

        /// <inheritdoc />
        public IClientBuilder ConfigureContainer<TContainerBuilder>(Action<TContainerBuilder> configureContainer)
        {
            this.serviceProviderBuilder.ConfigureContainer((HostBuilderContext context, TContainerBuilder containerBuilder) => configureContainer(containerBuilder));
            return this;
        }

        private static void ValidateSystemConfiguration(IServiceProvider serviceProvider)
        {
            var validators = serviceProvider.GetServices<IConfigurationValidator>();
            foreach (var validator in validators)
            {
                validator.ValidateConfiguration();
            }
        }

        private static void EnsureClientConfigurationConfigured(IServiceCollection services)
        {
            if (services.TryGetClientConfiguration() == null)
            {
                services.AddLegacyClientConfigurationSupport();
            }
        }
    }
}