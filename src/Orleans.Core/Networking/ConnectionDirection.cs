using System;

namespace Orleans.Messaging;

internal enum ConnectionDirection : byte
{
    SiloToSilo,
    ClientToGateway,
    GatewayToClient
}

/// <summary>
/// Identifies the Orleans messaging protocol carried by a transport.
/// </summary>
public enum TransportProtocol
{
    /// <summary>
    /// The protocol used for communication between cluster members.
    /// </summary>
    Cluster,

    /// <summary>
    /// The protocol used for communication between clients and gateways.
    /// </summary>
    Gateway
}

/// <summary>
/// Exposes the Orleans messaging protocol carried by a transport.
/// </summary>
public interface ITransportProtocolFeature
{
    /// <summary>
    /// Gets the protocol carried by the transport.
    /// </summary>
    public TransportProtocol Protocol { get; }
}

internal class TransportProtocolFeature : ITransportProtocolFeature
{
    private static readonly TransportProtocolFeature Cluster = new(TransportProtocol.Cluster);
    private static readonly TransportProtocolFeature Gateway = new(TransportProtocol.Gateway);

    public static TransportProtocolFeature Get(TransportProtocol protocol) => protocol switch
    {
        TransportProtocol.Cluster => Cluster,
        TransportProtocol.Gateway => Gateway,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
    };

    private TransportProtocolFeature(TransportProtocol protocol)
    {
        Protocol = protocol;
    }

    public TransportProtocol Protocol { get; }
}
