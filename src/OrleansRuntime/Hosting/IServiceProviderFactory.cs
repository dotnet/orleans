using System;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.Hosting
{
    /// <summary>
    /// Abstraction for integrating <see cref="IServiceProviderFactory{TContainerBuilder}"/> implementations.
    /// </summary>
    internal interface IServiceProviderFactory
    {
        /// <summary>
        /// Creates a <see cref="IServiceProvider"/> from an <see cref="IServiceCollection" />.
        /// </summary>
        /// <param name="services">The collection of services.</param>
        /// <returns>A <see cref="IServiceProvider" />.</returns>
        IServiceProvider BuildServiceProvider(IServiceCollection services);

        /// <summary>
        /// Adds a container configuration delegate.
        /// </summary>
        /// <typeparam name="TContainerBuilder">The container builder type.</typeparam>
        /// <param name="configureContainer">The container builder configuration delegate.</param>
        void ConfigureContainer<TContainerBuilder>(Action<TContainerBuilder> configureContainer);
    }
}