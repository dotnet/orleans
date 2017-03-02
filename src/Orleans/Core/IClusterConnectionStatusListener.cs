namespace Orleans
{
    /// <summary>
    /// Interface for notifying observers that connection to the cluster has been lost.
    /// </summary>
    internal interface IClusterConnectionStatusListener
    {
        /// <summary>
        /// Notifies this client that the connection to the cluster has been lost.
        /// </summary>
        void NotifyClusterConnectionLost();
    }
}