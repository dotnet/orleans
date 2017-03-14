namespace Microsoft.Orleans.ServiceFabric
{
    using Microsoft.ServiceFabric.Services.Communication.Runtime;

    using global::Orleans.Runtime.Configuration;

    /// <summary>
    /// Creates Service Fabric listeners for Orleans silos.
    /// </summary>
    public static class OrleansServiceListener
    {
        /// <summary>
        /// The Service Fabric endpoint name used by Orleans silos.
        /// </summary>
        internal const string OrleansServiceFabricEndpointName = "Orleans";

        /// <summary>
        /// Creates a <see cref="ServiceInstanceListener"/> which manages an Orleans silo for a stateless service.
        /// </summary>
        /// <param name="configuration">The Orleans cluster configuration.</param>
        /// <returns>A <see cref="ServiceInstanceListener"/> which manages an Orleans silo.</returns>
        public static ServiceInstanceListener CreateStateless(ClusterConfiguration configuration)
        {
            return new ServiceInstanceListener(
                context => new OrleansCommunicationListener(context, configuration),
                OrleansServiceFabricEndpointName);
        }

        /// <summary>
        /// Creates a <see cref="ServiceInstanceListener"/> which manages an Orleans silo for a stateless service.
        /// </summary>
        /// <param name="configuration">The Orleans cluster configuration.</param>
        /// <returns>A <see cref="ServiceInstanceListener"/> which manages an Orleans silo.</returns>
        public static ServiceReplicaListener CreateStateful(ClusterConfiguration configuration)
        {
            return new ServiceReplicaListener(
                context => new OrleansCommunicationListener(context, configuration),
                OrleansServiceFabricEndpointName);
        }
    }
}