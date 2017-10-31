namespace Orleans.ServiceFabric
{
    public static class FabricServiceConfiguration
    {
        /// <summary>
        /// The Service Fabric listener name used by Orleans silos.
        /// </summary>
        public const string ListenerName = "Orleans";

        /// <summary>
        /// The name used to identify the silo-to-silo communication endpoint.
        /// </summary>
        public const string SiloEndpointName = "OrleansSiloEndpoint";

        /// <summary>
        /// The name used to identify the client-to-silo communication endpoint.
        /// </summary>
        public const string GatewayEndpointName = "OrleansProxyEndpoint";
    }
}