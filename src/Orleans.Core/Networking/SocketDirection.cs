namespace Orleans.Messaging
{
    internal enum ConnectionDirection : byte
    {
        SiloToSilo,
        ClientToGateway,
        GatewayToClient
    }
}
