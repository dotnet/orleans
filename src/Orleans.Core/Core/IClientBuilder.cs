using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

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
        /// Set up the configuration for the builder itself. This will be used to initialize the <see cref="IHostingEnvironment"/>
        /// for use later in the build process. This can be called multiple times and the results will be additive.
        /// </summary>
        /// <param name="configureDelegate">The delegate for configuring the <see cref="IConfigurationBuilder"/> that will be used
        /// to construct the <see cref="IConfiguration"/> for the host.</param>
        /// <returns>The same instance of the host builder for chaining.</returns>
        IClientBuilder ConfigureHostConfiguration(Action<IConfigurationBuilder> configureDelegate);

        /// <summary>
        /// Sets up the configuration for the remainder of the build process and application. This can be called multiple times and
        /// the results will be additive. The results will be available at <see cref="HostBuilderContext.Configuration"/> for
        /// subsequent operations./>.
        /// </summary>
        /// <param name="configureDelegate">The delegate for configuring the <see cref="IConfigurationBuilder"/> that will be used
        /// to construct the <see cref="IConfiguration"/> for the application.</param>
        /// <returns>The same instance of the host builder for chaining.</returns>
        IClientBuilder ConfigureAppConfiguration(Action<HostBuilderContext, IConfigurationBuilder> configureDelegate);

        /// <summary>
        /// Adds services to the container. This can be called multiple times and the results will be additive.
        /// </summary>
        /// <param name="configureDelegate">The delegate for configuring the <see cref="IServiceCollection"/> that will be used
        /// to construct the <see cref="IServiceProvider"/>.</param>
        /// <returns>The same instance of the host builder for chaining.</returns>
        IClientBuilder ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureDelegate);
        
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