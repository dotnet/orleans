using System;

namespace Orleans
{
    /// <summary>
    /// Handler for client disconnection from a cluster.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The event arguments.</param>
    public delegate void ConnectionToClusterLostHandler(object sender, EventArgs e);

    /// <summary>
    /// Handler for the number of gateways.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The event arguments.</param>
    public delegate void GatewayCountChangedHandler(object sender, GatewayCountChangedEventArgs e);

    /// <summary>
    /// Event arguments for gateway connectivity events.
    /// </summary>
    public class GatewayCountChangedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the number of gateways which this client is currently connected to.
        /// </summary>
        public int NumberOfConnectedGateways { get; }

        /// <summary>
        /// Gets the number of gateways which this client was currently connected to before this event.
        /// </summary>
        public int PreviousNumberOfConnectedGateways { get; }

        /// <summary>
        /// Helper to detect situations where cluster connectivity was regained.
        /// </summary>
        public bool ConnectionRecovered => this.NumberOfConnectedGateways > 0 && this.PreviousNumberOfConnectedGateways <= 0;

        /// <summary>
        /// Initializes a new instance of the <see cref="GatewayCountChangedEventArgs"/> class.
        /// </summary>
        /// <param name="currentNumberOfConnectedGateways">
        /// The current number of connected gateways.
        /// </param>
        /// <param name="previousNumberOfConnectedGateways">
        /// The previous number of connected gateways.
        /// </param>
        public GatewayCountChangedEventArgs(int currentNumberOfConnectedGateways, int previousNumberOfConnectedGateways)
        {
            this.NumberOfConnectedGateways = currentNumberOfConnectedGateways;
            this.PreviousNumberOfConnectedGateways = previousNumberOfConnectedGateways;
        }
    }
}
