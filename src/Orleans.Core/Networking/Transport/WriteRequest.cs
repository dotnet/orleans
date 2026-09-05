#nullable enable

using System;
using Orleans.Serialization.Buffers;

namespace Orleans.Connections.Transport;

/// <summary>
/// Represents a write operation submitted to a <see cref="MessageTransport"/>.
/// </summary>
/// <remarks>
/// Once accepted by a transport, the request and its buffers remain transport-owned until a completion callback returns.
/// </remarks>
public abstract class WriteRequest
{
    /// <summary>
    /// Gets the buffers to write.
    /// </summary>
    /// <remarks>
    /// The contents remain valid and unchanged while the request is owned by the transport.
    /// </remarks>
    public ArcBufferReader Buffers { get; protected set; }
    internal virtual bool HasLargeMessages => false;

    /// <summary>
    /// Completes the request after all buffered data has been written.
    /// </summary>
    public abstract void SetResult();

    /// <summary>
    /// Completes the request with an error.
    /// </summary>
    /// <param name="error">The error which terminated the write.</param>
    public abstract void SetException(Exception error);
}
