#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Connections.Transport;

/// <summary>
/// Represents a bi-directional communication channel between two hosts.
/// </summary>
public abstract class MessageTransport : IAsyncDisposable
{
    /// <summary>
    /// Gets the cancellation token which is canceled once the connection is closed.
    /// </summary>
    public virtual CancellationToken Closed { get; }

    /// <summary>
    /// Gets a value indicating whether this instance is valid.
    /// </summary>
    public virtual bool IsValid => !Closed.IsCancellationRequested;

    /// <summary>
    /// Gets the collection of features available on the transport.
    /// </summary>
    public abstract IFeatureCollection Features { get; }

    /// <summary>
    /// Submits a read request to the channel.
    /// </summary>
    /// <param name="request">The read request.</param>
    /// <returns><see langword="true"/> if the read request was accepted by the channel, <see langword="false"/> if it was rejected.</returns>
    public abstract bool EnqueueRead(ReadRequest request);

    /// <summary>
    /// Submits a write request to the channel.
    /// </summary>
    /// <param name="request">The write request.</param>
    /// <returns><see langword="true"/> if the read request was accepted by the channel, <see langword="false"/> if it was rejected.</returns>
    public abstract bool EnqueueWrite(WriteRequest request);

    /// <summary>
    /// Closes the channel, optionally with a provided exception.
    /// </summary>
    /// <param name="closeException">The channel close exception, which is propagated to requests.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to force immediate shutdown.</param>
    /// <returns>A <see cref="ValueTask"/> which completes once the channel has been closed.</returns>
    public abstract ValueTask CloseAsync(Exception? closeException, CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return default;
    }
}

/// <summary>
/// Creates <see cref="MessageTransport"/> instances which are connected to a specified endpoint.
/// </summary>
public abstract class MessageTransportConnector : IAsyncDisposable
{
    /// <summary>
    /// Gets the collection of features available on the transport factory.
    /// </summary>
    public abstract IFeatureCollection Features { get; }

    /// <summary>
    /// Gets a value indicating whether this connector is valid for use.
    /// </summary>
    public abstract bool IsValid { get; }

    /// <summary>
    /// Creates a <see cref="MessageTransport"/> connected to the specified <paramref name="endpoint"/>.
    /// </summary>
    /// <param name="endpoint">The endpoint to connect to.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The connected message transport.</returns>
    public abstract ValueTask<MessageTransport> CreateAsync(EndPoint endpoint, CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return default;
    }
}

internal sealed class MessageTransportConnectorFactory(MessageTransportConnector connector, IEnumerable<TlsMessageTransportConnectorMiddleware> middlewares)
{
    public MessageTransportConnector GetMessageTransportConnector()
    {
        var result = connector;
        foreach (var middleware in middlewares.Reverse())
        {
            result = middleware.Apply(result);
        }

        return result;
    }
}

internal sealed class MessageTransportListenerFactory(MessageTransportListener connector, IEnumerable<TlsMessageTransportListenerMiddleware> middlewares)
{
    public MessageTransportListener GetMessageTransportListener()
    {
        var result = connector;
        foreach (var middleware in middlewares.Reverse())
        {
            result = middleware.Apply(result);
        }

        return result;
    }
}

/// <summary>
/// Middleware which operates on <see cref="MessageTransportConnector"/> instances.
/// </summary>
public interface IMessageTransportConnectorMiddleware
{
    /// <summary>
    /// Applies this middleware to the provided transport connector.
    /// </summary>
    /// <param name="transport">The transport connector.</param>
    /// <returns>The transport factory with this middleware applied to it.</returns>
    MessageTransportConnector Apply(MessageTransportConnector transport);
}

/// <summary>
/// Middleware which operates on <see cref="MessageTransportListener"/> instances.
/// </summary>
public interface IMessageTransportListenerMiddleware
{
    /// <summary>
    /// Applies this middleware to the provided listener.
    /// </summary>
    /// <param name="listener">The listener.</param>
    /// <returns>The listener with this middleware applied to it.</returns>
    MessageTransportListener Apply(MessageTransportListener listener);
}