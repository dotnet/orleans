namespace Orleans.Messaging
{
    internal enum SocketDirection
    {
        SiloToSilo,
        ClientToGateway,
        GatewayToClient
    }
}