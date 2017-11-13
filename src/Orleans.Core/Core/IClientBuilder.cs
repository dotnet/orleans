using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;

namespace Orleans
{
    /// <summary>
    /// Functionality for building <see cref="IClusterClient"/> instances.
    /// </summary>
    public interface IClientBuilder
    {
        /// <summary>
        /// A central location for sharing state between components during the client building process.
        /// </summary>
        IDictionary<object, object> Properties { get; }

        /// <summary>
        /// Builds the client.
        /// </summary>
        /// <remarks>This method may only be called once per builder instance.</remarks>
        /// <returns>The newly created client.</returns>
        IClusterClient Build();

        /// <summary>
        /// Adds a service configuration delegate to the configuration pipeline.
        /// </summary>
        /// <param name="configureServices">The service configuration delegate.</param>
        /// <returns>The builder.</returns>
        IClientBuilder ConfigureServices(Action<IServiceCollection> configureServices);

        /// <summary>
        /// Specified the configuration to use for this client.
        /// </summary>
        /// <param name="configuration">The configuration.</param>
        /// <remarks>This method may only be called once per builder instance.</remarks>
        /// <returns>The builder.</returns>
        IClientBuilder UseConfiguration(ClientConfiguration configuration);

        /// <summary>
        /// Specifies how the <see cref="IServiceProvider"/> for this client is configured. 
        /// </summary>
        /// <param name="factory">The service provider factory.</param>
        /// <returns>The builder.</returns>
        IClientBuilder UseServiceProviderFactory<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> factory);

        /// <summary>
        /// Adds a container configuration delegate.
        /// </summary>
        /// <typeparam name="TContainerBuilder">The container builder type.</typeparam>
        /// <param name="configureContainer">The container builder configuration delegate.</param>
        IClientBuilder ConfigureContainer<TContainerBuilder>(Action<TContainerBuilder> configureContainer);
    }
}