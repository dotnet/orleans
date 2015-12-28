using System;
using Orleans.Core;
using Orleans.Runtime;

namespace Orleans.Providers
{
    /// <summary>
    /// Interface to allow callbacks from providers into their assigned provider-manager.
    /// This allows access to runtime functionality, such as logging.
    /// </summary>
    /// <remarks>
    /// Passed to the provider during IProvider.Init call to that provider instance.
    /// </remarks>
    /// <seealso cref="IProvider"/>
    public interface IProviderRuntime
    {
        /// <summary>
        /// Provides a logger to be used by the provider. 
        /// </summary>
        /// <param name="loggerName">Name of the logger being requested.</param>
        /// <param name="logType">Type of the logger being requested.</param>
        /// <returns>Object reference to the requested logger.</returns>
        /// <seealso cref="TraceLogger.LoggerType"/>
        Logger GetLogger(string loggerName);

        /// <summary>
        /// Provides the ServiceId this cluster is running as.
        /// ServiceId's are intended to be long lived Id values for a particular service which will remain constant 
        /// even if the service is started / redeployed multiple times during its operations life.
        /// </summary>
        /// <returns>ServiceID Guid for this service.</returns>
        Guid ServiceId { get; }

        /// <summary>
        /// A unique identifier for the current silo.
        /// There is no semantic content to this string, but it may be useful for logging.
        /// </summary>
        string SiloIdentity { get; }

        /// <summary>
        /// Factory for getting references to grains.
        /// </summary>
        IGrainFactory GrainFactory { get; }

        /// <summary>
        /// Service provider for dependency injection
        /// </summary>
        IServiceProvider ServiceProvider { get; }
    }

    /// <summary>
    /// Provider-facing interface for manager of storage providers
    /// </summary>
    public interface IStorageProviderRuntime : IProviderRuntime
    {
        // for now empty, later can add storage specific runtime capabilities.
    }
}
