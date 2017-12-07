using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Hosting
{
    /// <summary>
    /// Functionality for building <see cref="IServiceProvider"/> instances.
    /// </summary>
    internal class ServiceProviderBuilder
    {
        private readonly List<Action<HostBuilderContext, IServiceCollection>> configureServicesDelegates = new List<Action<HostBuilderContext, IServiceCollection>>();
        private readonly List<object> configureContainerDelegates = new List<object>();
        private IServiceProviderFactoryAdapter serviceProviderFactory;

        /// <summary>
        /// Builds the service provider.
        /// </summary>
        /// <returns>The service provider.</returns>
        public IServiceProvider BuildServiceProvider(HostBuilderContext context)
        {
            // Configure the container.
            var services = new ServiceCollection();
            foreach (var configureServices in this.configureServicesDelegates)
            {
                configureServices(context, services);
            }

            // If no service provider factory has been specified, set a default.
            if (this.serviceProviderFactory == null)
            {
                this.UseDefaultServiceProviderFactory();
            }

            // Create the service provider using the configured factory.
            return this.serviceProviderFactory.BuildServiceProvider(context, services);
        }

        /// <summary>
        /// Adds a service configuration delegate to the configuration pipeline.
        /// </summary>
        /// <param name="configureServices">The service configuration delegate.</param>
        /// <returns>The builder.</returns>
        public void ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureServices)
        {
            if (configureServices == null) throw new ArgumentNullException(nameof(configureServices));
            this.configureServicesDelegates.Add(configureServices);
        }

        /// <summary>
        /// Specifies how the <see cref="IServiceProvider"/> is configured. 
        /// </summary>
        /// <param name="factory">The service provider factory.</param>
        /// <returns>The builder.</returns>
        public void UseServiceProviderFactory<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> factory)
        {
            if (this.serviceProviderFactory != null)
                throw new InvalidOperationException("The service provider factory has already been specified.");
            this.serviceProviderFactory = new ServiceProviderFactoryAdapter<TContainerBuilder>(factory);
            foreach (var builder in this.configureContainerDelegates)
            {
                var typedDelegate = (Action<HostBuilderContext, TContainerBuilder>)builder;
                this.serviceProviderFactory.ConfigureContainer(typedDelegate);
            }

            this.configureContainerDelegates.Clear();
        }

        /// <summary>
        /// Adds a container configuration delegate.
        /// </summary>
        /// <typeparam name="TContainerBuilder">The container builder type.</typeparam>
        /// <param name="configureContainer">The container builder configuration delegate.</param>
        public void ConfigureContainer<TContainerBuilder>(Action<HostBuilderContext, TContainerBuilder> configureContainer)
        {
            if (this.serviceProviderFactory != null) this.serviceProviderFactory.ConfigureContainer(configureContainer);
            else this.configureContainerDelegates.Add(configureContainer);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void UseDefaultServiceProviderFactory()
        {
            var factory = new DelegateServiceProviderFactory(svc => svc.BuildServiceProvider());
            this.UseServiceProviderFactory(factory);
        }
    }
}