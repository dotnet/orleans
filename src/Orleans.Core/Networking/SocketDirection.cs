namespace Orleans.Messaging
{
    internal enum ConnectionDirection
    {
        SiloToSilo,
        ClientToGateway,
        GatewayToClient
    }
}
