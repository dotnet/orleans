using System;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Hosting
{
    /// <summary>
    /// Common interface for <see cref="IServiceProviderFactory{TContainerBuilder}"/> implementations.
    /// </summary>
    internal interface IServiceProviderFactoryAdapter
    {
        /// <summary>
        /// Creates a <see cref="IServiceProvider"/> from an <see cref="IServiceCollection" />.
        /// </summary>
        /// <param name="context">The host builder context.</param>
        /// <param name="services">The collection of services.</param>
        /// <returns>A <see cref="IServiceProvider" />.</returns>
        IServiceProvider BuildServiceProvider(HostBuilderContext context, IServiceCollection services);

        /// <summary>
        /// Adds a container configuration delegate.
        /// </summary>
        /// <typeparam name="TContainerBuilder">The container builder type.</typeparam>
        /// <param name="configureContainer">The container builder configuration delegate.</param>
        void ConfigureContainer<TContainerBuilder>(Action<HostBuilderContext, TContainerBuilder> configureContainer);
    }
}