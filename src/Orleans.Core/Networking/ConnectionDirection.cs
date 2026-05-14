using System;

namespace Orleans.Messaging
{
    internal enum ConnectionDirection : byte
    {
        SiloToSilo,
        ClientToGateway,
        GatewayToClient
    }

    public enum TransportProtocol
    {
        Cluster,
        Gateway
    }

    public interface ITransportProtocolFeature
    {
        public TransportProtocol Protocol { get; }
    }

    internal class TransportProtocolFeature : ITransportProtocolFeature
    {
        private static readonly TransportProtocolFeature Cluster = new (TransportProtocol.Cluster);
        private static readonly TransportProtocolFeature Gateway = new (TransportProtocol.Gateway);

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
}
