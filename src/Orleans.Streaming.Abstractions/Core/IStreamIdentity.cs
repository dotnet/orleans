using System;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Uniquely identifies a stream.
    /// </summary>
    /// <remarks>
    /// Use <see cref="StreamId"/> instead, where possible.
    /// </remarks>
    public interface IStreamIdentity
    {
        /// <summary>
        /// Gets the unique identifier.
        /// </summary>
        /// <value>The unique identifier.</value>
        Guid Guid { get; }

        /// <summary>
        /// Gets the namespace.
        /// </summary>
        /// <value>The namespace.</value>
        string Namespace { get; }
    }
}
