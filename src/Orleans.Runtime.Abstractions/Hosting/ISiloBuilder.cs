using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Hosting
{
    /// <summary>
    /// Functionality for building <see cref="ISiloHost"/> instances.
    /// </summary>
    public interface ISiloBuilder
    {
        /// <summary>
        /// A central location for sharing state between components during the silo building process.
        /// </summary>
        IDictionary<object, object> Properties { get; }

        /// <summary>
        /// Configures services in the container. This can be called multiple times and the results will be additive.
        /// </summary>
        /// <param name="configureDelegate">The delegate for configuring the <see cref="IServiceCollection"/> that will be used
        /// to construct the <see cref="IServiceProvider"/>.</param>
        /// <returns>The same instance of the silo builder for chaining.</returns>
        ISiloBuilder ConfigureServices(Action<Microsoft.Extensions.Hosting.HostBuilderContext, IServiceCollection> configureDelegate);
    }
}