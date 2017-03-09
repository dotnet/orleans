namespace Orleans.Messaging
{
    /// <summary>
    /// An optional interface that GatewayListProvider may implement if it support out of band gw update notifications.
    /// By default GatewayListProvider should suppport pull based queries (GetGateways).
    /// Optionally, some GatewayListProviders may be able to notify a listener if an updated gw information is available.
    /// This is optional and not required.
    /// </summary>
    public interface IGatewayListObservable
    {
        bool SubscribeToGatewayNotificationEvents(IGatewayListListener listener);

        bool UnSubscribeFromGatewayNotificationEvents(IGatewayListListener listener);
    }
}