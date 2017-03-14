using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;

namespace Orleans
{
    /// <summary>
    /// Functionality for building <see cref="IClusterClient"/> instances.
    /// </summary>
    public interface IClientBuilder
    {
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
    }
}