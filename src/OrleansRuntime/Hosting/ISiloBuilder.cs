using System;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.Hosting
{
    /// <summary>
    /// Functionality for building <see cref="ISilo"/> instances.
    /// </summary>
    public interface ISiloBuilder
    {
        /// <summary>
        /// Builds the silo.
        /// </summary>
        /// <remarks>This method may only be called once per builder instance.</remarks>
        /// <returns>The newly created silo.</returns>
        ISilo Build();

        /// <summary>
        /// Adds a service configuration delegate to the configuration pipeline.
        /// </summary>
        /// <param name="configureServices">The service configuration delegate.</param>
        /// <returns>The builder.</returns>
        ISiloBuilder ConfigureServices(Action<IServiceCollection> configureServices);

        /// <summary>
        /// Specifies how the <see cref="IServiceProvider"/> for this silo is configured. 
        /// </summary>
        /// <param name="configureServiceProvider">The service provider configuration method.</param>
        /// <returns>The builder.</returns>
        ISiloBuilder UseServiceProviderFactory(Func<IServiceCollection, IServiceProvider> configureServiceProvider);
    }
}