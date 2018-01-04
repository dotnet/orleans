using System;
using System.Threading.Tasks;
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

        /// <summary>
        /// Binds an extension to an addressable object, if not already done.
        /// </summary>
        /// <typeparam name="TExtension">The type of the extension (e.g. StreamConsumerExtension).</typeparam>
        /// <typeparam name="TExtensionInterface">The public interface type of the implementation.</typeparam>
        /// <param name="newExtensionFunc">A factory function that constructs a new extension object.</param>
        /// <returns>A tuple, containing first the extension and second an addressable reference to the extension's interface.</returns>
        Task<Tuple<TExtension, TExtensionInterface>> BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : IGrainExtension
            where TExtensionInterface : IGrainExtension;
    }

    /// <summary>
    /// Provider-facing interface for manager of storage providers
    /// </summary>
    public interface IStorageProviderRuntime : IProviderRuntime
    {
        // for now empty, later can add storage specific runtime capabilities.
    }

    /// <summary>
    /// Provider-facing interface for log consistency
    /// </summary>
    public interface ILogConsistencyProviderRuntime : IProviderRuntime
    {
        // for now empty, later can add provider specific runtime capabilities.
    }
}
