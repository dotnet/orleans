using System;
using Orleans.CodeGeneration;
using Orleans.Streams;

namespace Orleans
{
    /// <summary>
    /// The internal-facing client interface.
    /// </summary>
    internal interface IInternalClusterClient : IClusterClient, IInternalGrainFactory
    {
        /// <summary>
        /// Gets the client's <see cref="IStreamProviderRuntime"/>.
        /// </summary>
        IStreamProviderRuntime StreamProviderRuntime { get; }
    }
}