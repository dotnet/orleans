namespace Orleans.Core;

/// <summary>
/// Interface that receives notifications about the status of the cluster connection.
/// </summary>
public interface IClusterConnectionStatusObserver
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
    /// <param name="connectionRecovered">Helper to detect situations where cluster connectivity was regained.</param>
    void NotifyGatewayCountChanged(int currentNumberOfGateways, int previousNumberOfGateways, bool connectionRecovered);

    /// <summary>
    /// Notifies this client that the connection to the cluster has been lost.
    /// </summary>
    void NotifyClusterConnectionLost();
}