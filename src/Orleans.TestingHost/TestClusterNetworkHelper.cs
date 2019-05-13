namespace Orleans.TestingHost
{
    /// <summary>
    /// Methods for assisting in networking configuration for test clusters.
    /// </summary>
    public static class TestClusterNetworkHelper
    {
        /// <summary>
        /// Returns two ports which are not currently in use by any TCP listeners.
        /// </summary>
        public static (int siloPort, int gatewayPort) GetRandomAvailableServerPorts() => TestClusterBuilder.GetAvailableConsecutiveServerPortsPair(1);
    }
}