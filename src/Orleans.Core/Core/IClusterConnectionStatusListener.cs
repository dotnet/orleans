namespace Orleans
{
    /// <summary>
    /// Interface for notifying observers that connection to the cluster has been lost.
    /// </summary>
    internal interface IClusterConnectionStatusListener
    {
        /// <summary>
        /// Notifies this client that the number of connected gateways has changed
        /// </summary>
        /// <param name="currentNumberOfGateways">
        /// The current number of gateways.
        /// </param>
        /// <param name="previousNumberOfGateways">
        /// The previous number of gateways.
        /// </param>
        void NotifyGatewayCountChanged(int currentNumberOfGateways, int previousNumberOfGateways);

        /// <summary>
        /// Notifies this client that the connection to the cluster has been lost.
        /// </summary>
        void NotifyClusterConnectionLost();
    }
}