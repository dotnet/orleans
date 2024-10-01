namespace Orleans;

/// <summary>
/// Interface that receives notifications about the status of the cluster connection.
/// </summary>
public interface IClusterConnectionStatusObserver
{
    /// <summary>
    /// Notifies this observer that the number of connected gateways has changed.
    /// </summary>
    /// <param name="currentNumberOfGateways">
    /// The current number of gateways.
    /// </param>
    /// <param name="previousNumberOfGateways">
    /// The previous number of gateways.
    /// </param>
    /// <param name="connectionRecovered">Indicates whether a loss of connectivity has been resolved.</param>
    void NotifyGatewayCountChanged(int currentNumberOfGateways, int previousNumberOfGateways, bool connectionRecovered);

    /// <summary>
    /// Notifies this observer that the connection to the cluster has been lost.
    /// </summary>
    void NotifyClusterConnectionLost();
}