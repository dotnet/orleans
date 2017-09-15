using System;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Hosting
{
    /// <summary>
    /// Functionality for building <see cref="ISilo"/> instances.
    /// </summary>
    public interface ISiloHostBuilder
    {
        /// <summary>
        /// Builds the silo.
        /// </summary>
        /// <remarks>This method may only be called once per builder instance.</remarks>
        /// <returns>The newly created silo host.</returns>
        ISilo Build();

        /// <summary>
        /// Adds a service configuration delegate to the configuration pipeline.
        /// </summary>
        /// <param name="configureServices">The service configuration delegate.</param>
        /// <returns>The silo builder.</returns>
        ISiloHostBuilder ConfigureServices(Action<IServiceCollection> configureServices);

        /// <summary>
        /// Specifies how the <see cref="IServiceProvider"/> for this silo is configured. 
        /// </summary>
        /// <param name="factory">The service provider factory.</param>
        /// <returns>The silo builder.</returns>
        ISiloHostBuilder UseServiceProviderFactory<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> factory);

        /// <summary>
        /// Adds a container configuration delegate.
        /// </summary>
        /// <typeparam name="TContainerBuilder">The container builder type.</typeparam>
        /// <param name="configureContainer">The container builder configuration delegate.</param>
        ISiloHostBuilder ConfigureContainer<TContainerBuilder>(Action<TContainerBuilder> configureContainer);
    }
}