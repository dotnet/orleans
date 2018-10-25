using System;
using System.Collections.Generic;
using System.Text;
using Orleans.CodeGeneration;

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

    // Used to notify the client that the count of gateways has changed

    public class GatewayCountChangedEventArgs : EventArgs
    {
        /// <summary>
        /// The number of gateways which this client is currently connected to.
        /// </summary>
        public int NumberOfConnectedGateways { get; }

        /// <summary>
        /// The number of gateways which this client was currently connected to before this event.
        /// </summary>
        public int PreviousNumberOfConnectedGateways { get; }

        /// <summary>
        /// Helper to detect situations where we (probably) regained connection to the cluster.
        /// </summary>
        public bool ConnectionRecovered => NumberOfConnectedGateways > 0 && PreviousNumberOfConnectedGateways <= 0;

        public GatewayCountChangedEventArgs(int currentNumberOfConnectedGateways, int previousNumberOfConnectedGateways)
        {
            NumberOfConnectedGateways = currentNumberOfConnectedGateways;
            PreviousNumberOfConnectedGateways = previousNumberOfConnectedGateways;
        }
    }

    /// <summary>
    /// The delegate called before every request to a grain.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="grain">The grain.</param>
    public delegate void ClientInvokeCallback(InvokeMethodRequest request, IGrain grain);
}
