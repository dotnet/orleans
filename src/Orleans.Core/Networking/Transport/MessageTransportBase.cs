#nullable enable

namespace Orleans.Connections.Transport;

/// <summary>
/// Base class for <see cref="MessageTransport"/> implementations.
/// </summary>
public abstract class MessageTransportBase : MessageTransport
{
    /// <summary>
    /// Gets the features of this transport.
    /// </summary>
    public override FeatureCollection Features { get; } = new FeatureCollection();
}
