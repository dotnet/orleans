using System;
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
    public interface IProviderRuntime
    {
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
        (TExtension, TExtensionInterface) BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : TExtensionInterface
            where TExtensionInterface : IGrainExtension;
    }
}
