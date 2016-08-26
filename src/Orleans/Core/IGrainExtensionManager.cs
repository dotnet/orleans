namespace Orleans.Core
{
    using System;

    using Orleans.Runtime;

    /// <summary>
    /// Methods for managing grain extensions for the current activation.
    /// </summary>
    internal interface IGrainExtensionManager
    {
        /// <summary>
        /// Gets the <typeparamref name="TExtension"/> extension handler for the current grain, returning <see langword="true"/> if successful
        /// or <see langword="false"/> otherwise.
        /// </summary>
        /// <typeparam name="TExtension">The extension interface type.</typeparam>
        /// <param name="result">The extension handler.</param>
        /// <returns><see langword="true"/> if successful or <see langword="false"/> otherwise.</returns>
        bool TryGetExtensionHandler<TExtension>(out TExtension result) where TExtension : IGrainExtension;

        /// <summary>
        /// Adds an extension handler to the current grain.
        /// </summary>
        /// <param name="handler">The extension handler to add.</param>
        /// <param name="extensionType">The extension interface type which the handler will be registered for.</param>
        /// <returns><see langword="true"/> if successful or <see langword="false"/> otherwise.</returns>
        bool TryAddExtension(IGrainExtension handler, Type extensionType);

        /// <summary>
        /// Removes an extension handler from the current grain.
        /// </summary>
        /// <param name="handler">The extension handler to remove.</param>
        void RemoveExtension(IGrainExtension handler);
    }
}