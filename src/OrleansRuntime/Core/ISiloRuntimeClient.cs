using System;
using System.Threading.Tasks;
using Orleans.Streams;

namespace Orleans.Runtime
{
    /// <summary>
    /// Runtime client methods accessible on silos.
    /// </summary>
    internal interface ISiloRuntimeClient : IRuntimeClient
    {
        /// <summary>
        /// Gets the stream directory.
        /// </summary>
        /// <returns>The stream directory.</returns>
        StreamDirectory GetStreamDirectory();
        
        /// <summary>
        /// Attempts to add the provided extension handler to the currently executing grain.
        /// </summary>
        /// <param name="handler">The extension handler.</param>
        /// <returns><see langword="true"/> if the operation succeeded; <see langword="false" /> otherwise.</returns>
        bool TryAddExtension(IGrainExtension handler);

        /// <summary>
        /// Attempts to retrieve the specified extension type from the currently executing grain.
        /// </summary>
        /// <typeparam name="TExtension">The type of the extension.</typeparam>
        /// <param name="result">The extension, or <see langword="null" /> if it was not available.</param>
        /// <returns><see langword="true"/> if the operation succeeded; <see langword="false" /> otherwise.</returns>
        bool TryGetExtensionHandler<TExtension>(out TExtension result) where TExtension : IGrainExtension;

        /// <summary>
        /// Removes the provided extension handler from the currently executing grain.
        /// </summary>
        /// <param name="handler">The extension handler to remove.</param>
        void RemoveExtension(IGrainExtension handler);

        /// <summary>
        /// Binds an extension to the currently executing grain if it does not already have an extension of the specified
        /// <typeparamref name="TExtensionInterface"/>.
        /// </summary>
        /// <typeparam name="TExtension">The type of the extension (e.g. StreamConsumerExtension).</typeparam>
        /// <typeparam name="TExtensionInterface">The public interface type of the implementation.</typeparam>
        /// <param name="newExtensionFunc">A factory function that constructs a new extension object.</param>
        /// <returns>A tuple, containing first the extension and second an addressable reference to the extension's interface.</returns>
        Task<Tuple<TExtension, TExtensionInterface>> BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : IGrainExtension
            where TExtensionInterface : IGrainExtension;

        IActivationData CurrentActivationData { get; }

        void DeactivateOnIdle(ActivationId id);
    }
}