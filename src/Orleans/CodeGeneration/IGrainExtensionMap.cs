using Orleans.Runtime;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// Methods for querying a collection of grain extensions.
    /// </summary>
    public interface IGrainExtensionMap
    {
        /// <summary>
        /// Gets the extension from this instance if it is available.
        /// </summary>
        /// <param name="interfaceId">The interface id.</param>
        /// <param name="extension">The extension.</param>
        /// <returns>
        /// <see langword="true"/> if the extension is found, <see langword="false"/> otherwise.
        /// </returns>
        bool TryGetExtension(int interfaceId, out IGrainExtension extension);
    }
}