#nullable enable

using System.Net;

namespace Orleans.Connections.Transport;

/// <summary>
/// Exposes local and remote endpoints for a <see cref="MessageTransport"/>.
/// </summary>
public interface IConnectionEndPointFeature
{
    /// <summary>
    /// Gets or sets the local endpoint.
    /// </summary>
    EndPoint? LocalEndPoint { get; set; }

    /// <summary>
    /// Gets or sets the remote endpoint.
    /// </summary>
    EndPoint? RemoteEndPoint { get; set; }
}

internal sealed class ConnectionEndPointFeature : IConnectionEndPointFeature
{
    public EndPoint? LocalEndPoint { get; set; }

    public EndPoint? RemoteEndPoint { get; set; }
}
