using System;
using Orleans.Runtime;

namespace Orleans.Providers
{
    /// <summary>
    /// <see cref="IProviderRuntime"/> for clients.
    /// </summary>
    /// <seealso cref="Orleans.Providers.IProviderRuntime" />
    /// <remarks>
    /// Initializes a new instance of the <see cref="ClientProviderRuntime"/> class.
    /// </remarks>
    /// <param name="grainFactory">The grain factory.</param>
    /// <param name="serviceProvider">The service provider.</param>
    /// <param name="clientContext">The client context.</param>
    internal class ClientProviderRuntime(
        IInternalGrainFactory grainFactory,
        IServiceProvider serviceProvider,
        ClientGrainContext clientContext) : IProviderRuntime
    {

        /// <inheritdoc/>
        public IGrainFactory GrainFactory => grainFactory;

        /// <inheritdoc/>
        public IServiceProvider ServiceProvider { get; } = serviceProvider;

        /// <inheritdoc/>
        public (TExtension Extension, TExtensionInterface ExtensionReference) BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : class, TExtensionInterface
            where TExtensionInterface : class, IGrainExtension
        {
            return clientContext.GetOrSetExtension<TExtension, TExtensionInterface>(newExtensionFunc);
        }
    }
}
