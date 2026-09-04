#nullable enable

using System;
using Orleans.Serialization.Buffers;

namespace Orleans.Connections.Transport;

/// <summary>
/// Represents a read operation submitted to a <see cref="MessageTransport"/>.
/// </summary>
/// <remarks>
/// Once accepted by a transport, the request remains transport-owned until one of its terminal callbacks completes.
/// </remarks>
public abstract class ReadRequest
{
    /// <summary>
    /// Consumes available input for this request.
    /// </summary>
    /// <param name="buffer">The available input. Implementations advance this reader as bytes are consumed.</param>
    /// <returns><see langword="true"/> when the request is complete; otherwise, <see langword="false"/> to await more input.</returns>
    public abstract bool OnRead(ArcBufferReader buffer);

    /// <summary>
    /// Completes the request with an error.
    /// </summary>
    /// <param name="error">The error which terminated the read.</param>
    public abstract void OnError(Exception error);

    /// <summary>
    /// Completes the request because the transport was canceled or closed.
    /// </summary>
    public abstract void OnCanceled();
}
