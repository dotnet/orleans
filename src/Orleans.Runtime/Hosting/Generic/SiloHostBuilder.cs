using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.ApplicationParts;

namespace Orleans.Hosting
{
    /// <summary>
    /// Functionality for building <see cref="ISiloHost"/> instances.
    /// </summary>
    public class SiloHostBuilder : ISiloHostBuilder
    {
        private readonly ServiceProviderBuilder serviceProviderBuilder = new ServiceProviderBuilder();

        private readonly List<Action<IConfigurationBuilder>> configureHostConfigActions = new List<Action<IConfigurationBuilder>>();
        private readonly List<Action<HostBuilderContext, IConfigurationBuilder>> configureAppConfigActions = new List<Action<HostBuilderContext, IConfigurationBuilder>>();
        private HostBuilderContext hostBuilderContext;
        private IConfiguration hostConfiguration;
        private IConfiguration appConfiguration;
        private IHostingEnvironment hostingEnvironment;
        private bool built;

        /// <inheritdoc />
        public IDictionary<object, object> Properties { get; } = new Dictionary<object, object>();

        /// <inheritdoc />
        public ISiloHost Build()
        {
            if (this.built)
                throw new InvalidOperationException($"{nameof(this.Build)} can only be called once per {nameof(SiloHostBuilder)} instance.");
            this.built = true;

            // Automatically configure Orleans if it wasn't configured before. 
            // This will not happen once we use the generic host builder from Microsoft.Extensions.Hosting
            this.ConfigureDefaults();

            BuildHostConfiguration();
            CreateHostingEnvironment();
            CreateHostBuilderContext();
            BuildAppConfiguration();

            var serviceProvider = CreateServiceProvider();

            ValidateSystemConfiguration(serviceProvider);

            // Construct and return the silo.
            return serviceProvider.GetRequiredService<ISiloHost>();
        }

        /// <inheritdoc />
        public ISiloHostBuilder ConfigureHostConfiguration(Action<IConfigurationBuilder> configureDelegate)
        {
            this.configureHostConfigActions.Add(configureDelegate ?? throw new ArgumentNullException(nameof(configureDelegate)));
            return this;
        }

        /// <inheritdoc />
        public ISiloHostBuilder ConfigureAppConfiguration(Action<HostBuilderContext, IConfigurationBuilder> configureDelegate)
        {
            this.configureAppConfigActions.Add(configureDelegate ?? throw new ArgumentNullException(nameof(configureDelegate)));
            return this;
        }

        /// <inheritdoc />
        public ISiloHostBuilder ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureDelegate)
        {
            if (configureDelegate == null) throw new ArgumentNullException(nameof(configureDelegate));
            this.serviceProviderBuilder.ConfigureServices(configureDelegate);
            return this;
        }

        /// <inheritdoc />
        public ISiloHostBuilder UseServiceProviderFactory<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> factory)
        {
            this.serviceProviderBuilder.UseServiceProviderFactory(factory);
            return this;
        }

        /// <inheritdoc />
        public ISiloHostBuilder ConfigureContainer<TContainerBuilder>(Action<HostBuilderContext, TContainerBuilder> configureDelegate)
        {
            this.serviceProviderBuilder.ConfigureContainer(configureDelegate);
            return this;
        }

        private void CreateHostBuilderContext()
        {
            this.hostBuilderContext = new HostBuilderContext(this.Properties)
            {
                HostingEnvironment = this.hostingEnvironment,
                Configuration = this.hostConfiguration
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

        private void CreateHostingEnvironment()
        {
            this.hostingEnvironment = new HostingEnvironment()
            {
                ApplicationName = this.hostConfiguration[HostDefaults.ApplicationKey],
                EnvironmentName = this.hostConfiguration[HostDefaults.EnvironmentKey] ?? EnvironmentName.Production,
            };
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

        private IServiceProvider CreateServiceProvider()
        {
            this.ConfigureServices(
                services =>
                {
                    services.AddSingleton(this.hostingEnvironment);
                    services.AddSingleton(this.hostBuilderContext);
                    services.AddSingleton(this.appConfiguration);
                    services.AddSingleton<IHostApplicationLifetime, SiloApplicationLifetime>();
                    services.AddOptions();
                    services.AddLogging();
                });
            var serviceProvider = this.serviceProviderBuilder.BuildServiceProvider(this.hostBuilderContext);
            return serviceProvider;
        }

        private static void ValidateSystemConfiguration(IServiceProvider serviceProvider)
        {
            var validators = serviceProvider.GetServices<IConfigurationValidator>();
            foreach (var validator in validators)
            {
                validator.ValidateConfiguration();
            }
        }
    }
}
