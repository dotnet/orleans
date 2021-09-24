using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        /// Adds services to the container. This can be called multiple times and the results will be additive.
        /// </summary>
        /// <param name="configureDelegate">The delegate for configuring the <see cref="IServiceCollection"/> that will be used
        /// to construct the <see cref="IServiceProvider"/>.</param>
        /// <returns>The same instance of the host builder for chaining.</returns>
        IClientBuilder ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureDelegate);
    }
}