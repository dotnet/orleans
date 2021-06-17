using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Serialization;
using Microsoft.Extensions.Hosting;
using IHostingEnvironment = Orleans.Hosting.IHostingEnvironment;
using HostBuilderContext = Orleans.Hosting.HostBuilderContext;
using HostDefaults = Orleans.Hosting.HostDefaults;
using EnvironmentName = Orleans.Hosting.EnvironmentName;

namespace Orleans
{
    /// <summary>
    /// Builder used for creating <see cref="IClusterClient"/> instances.
    /// </summary>
    public class ClientBuilder : IClientBuilder
    {
        private readonly ServiceProviderBuilder serviceProviderBuilder = new ServiceProviderBuilder();
        private readonly List<Action<IConfigurationBuilder>> configureHostConfigActions = new List<Action<IConfigurationBuilder>>();
        private readonly List<Action<HostBuilderContext, IConfigurationBuilder>> configureAppConfigActions = new List<Action<HostBuilderContext, IConfigurationBuilder>>();
        private HostBuilderContext hostBuilderContext;
        private IConfiguration hostConfiguration;
        private IConfiguration appConfiguration;
        private IHostingEnvironment hostingEnvironment;
        private bool built;

        public ClientBuilder()
        {
            this.ConfigureDefaults();
        }
        
        /// <inheritdoc />
        public IDictionary<object, object> Properties { get; } = new Dictionary<object, object>();

        /// <inheritdoc />
        public IClusterClient Build()
        {
            if (this.built) throw new InvalidOperationException($"{nameof(this.Build)} may only be called once per {nameof(ClientBuilder)} instance.");
            this.built = true;

            BuildHostConfiguration();
            CreateHostingEnvironment();
            CreateHostBuilderContext();
            BuildAppConfiguration();

            this.ConfigureServices(
                services =>
                {
                    services.AddSingleton(this.hostingEnvironment);
                    services.AddSingleton(this.hostBuilderContext);
                    services.AddSingleton(this.appConfiguration);
                    services.AddSingleton<IHostApplicationLifetime, ClientApplicationLifetime>();
                    services.AddOptions();
                    services.AddLogging();
                });

            var serviceProvider = this.serviceProviderBuilder.BuildServiceProvider(this.hostBuilderContext);
            ValidateSystemConfiguration(serviceProvider);

            // Construct and return the cluster client.
            serviceProvider.GetRequiredService<OutsideRuntimeClient>().ConsumeServices(serviceProvider);
            return serviceProvider.GetRequiredService<IClusterClient>();
        }

        /// <inheritdoc />
        public IClientBuilder ConfigureHostConfiguration(Action<IConfigurationBuilder> configureDelegate)
        {
            this.configureHostConfigActions.Add(configureDelegate ?? throw new ArgumentNullException(nameof(configureDelegate)));
            return this;
        }

        /// <inheritdoc />
        public IClientBuilder ConfigureAppConfiguration(Action<HostBuilderContext, IConfigurationBuilder> configureDelegate)
        {
            this.configureAppConfigActions.Add(configureDelegate ?? throw new ArgumentNullException(nameof(configureDelegate)));
            return this;
        }

        /// <inheritdoc />
        public IClientBuilder ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureDelegate)
        {
            if (configureDelegate == null) throw new ArgumentNullException(nameof(configureDelegate));
            this.serviceProviderBuilder.ConfigureServices(configureDelegate);
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

        private void CreateHostBuilderContext()
        {
            this.hostBuilderContext = new HostBuilderContext(this.Properties)
            {
                HostingEnvironment = this.hostingEnvironment,
                Configuration = this.hostConfiguration
            };
        }

        private void CreateHostingEnvironment()
        {
            this.hostingEnvironment = new HostingEnvironment()
            {
                ApplicationName = this.hostConfiguration[HostDefaults.ApplicationKey],
                EnvironmentName = this.hostConfiguration[HostDefaults.EnvironmentKey] ?? EnvironmentName.Production,
            };
        }

        private void BuildHostConfiguration()
        {
            var configBuilder = new ConfigurationBuilder();
            foreach (var buildAction in this.configureHostConfigActions)
            {
                buildAction(configBuilder);
            }
            this.hostConfiguration = configBuilder.Build();
        }

        private void BuildAppConfiguration()
        {
            var configBuilder = new ConfigurationBuilder();

            // replace with: configBuilder.AddConfiguration(this.hostConfiguration);
            // This method was added post v2.0.0 of Microsoft.Extensions.Configuration
            foreach (var buildAction in this.configureHostConfigActions)
            {
                buildAction(configBuilder);
            }
            // end replace

            foreach (var buildAction in this.configureAppConfigActions)
            {
                buildAction(this.hostBuilderContext, configBuilder);
            }
            this.appConfiguration = configBuilder.Build();
            this.hostBuilderContext.Configuration = this.appConfiguration;
        }
    }
}