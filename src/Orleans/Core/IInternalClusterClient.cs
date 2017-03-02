using Orleans.Streams;

namespace Orleans
{
    /// <summary>
    /// The internal-facing client interface.
    /// </summary>
    internal interface IInternalClusterClient : IClusterClient
    {
        /// <summary>
        /// Gets the client's <see cref="IInternalGrainFactory"/>.
        /// </summary>
        IInternalGrainFactory InternalGrainFactory { get; }

        /// <summary>
        /// Gets the client's <see cref="IStreamProviderRuntime"/>.
        /// </summary>
        IStreamProviderRuntime StreamProviderRuntime { get; }
    }
}