using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Hosting
{
    /// <summary>
    /// Adapts an <see cref="IServiceProviderFactory{TContainerBuilder}"/> into an <see cref="IServiceProviderFactoryAdapter"/>.
    /// </summary>
    /// <typeparam name="TContainerBuilder">The container builder type.</typeparam>
    internal class ServiceProviderFactoryAdapter<TContainerBuilder> : IServiceProviderFactoryAdapter
    {
        private readonly IServiceProviderFactory<TContainerBuilder> serviceProviderFactory;
        private readonly List<Action<HostBuilderContext, TContainerBuilder>> configureContainerDelegates = new List<Action<HostBuilderContext, TContainerBuilder>>();

        public ServiceProviderFactoryAdapter(IServiceProviderFactory<TContainerBuilder> serviceProviderFactory)
        {
            this.serviceProviderFactory = serviceProviderFactory;
        }

        /// <inheritdoc />
        public IServiceProvider BuildServiceProvider(HostBuilderContext context, IServiceCollection services)
        {
            var builder = this.serviceProviderFactory.CreateBuilder(services);

            foreach (var configureContainer in this.configureContainerDelegates)
            {
                configureContainer(context, builder);
            }

            return this.serviceProviderFactory.CreateServiceProvider(builder);
        }

        /// <inheritdoc />
        public void ConfigureContainer<TBuilder>(Action<HostBuilderContext, TBuilder> configureContainer)
        {
            if (configureContainer == null) throw new ArgumentNullException(nameof(configureContainer));
            var typedDelegate = configureContainer as Action<HostBuilderContext, TContainerBuilder>;
            if (typedDelegate == null)
            {
                var msg = $"Type of configuration delegate requires builder of type {typeof(TBuilder)} which does not match previously configured container builder type {typeof(TContainerBuilder)}.";
                throw new ArgumentException(msg, nameof(configureContainer));
            }

            this.configureContainerDelegates.Add(typedDelegate);
        }
    }
}