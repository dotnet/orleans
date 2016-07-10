using Orleans.Runtime.Configuration;

namespace Microsoft.Orleans.ServiceFabric
{
    using System.Fabric;

    using Microsoft.ServiceFabric.Services.Communication.Runtime;

    public static class OrleansServiceListener
    {
        internal const string OrleansServiceFabricEndpointName = "Orleans";

        public static ServiceReplicaListener CreateStateful(ClusterConfiguration configuration, IServicePartition partition)
        {
            return new ServiceReplicaListener(context => new OrleansCommunicationListener(context, configuration, partition),
                                              OrleansServiceFabricEndpointName,
                                              listenOnSecondary: false);
        }

        public static ServiceInstanceListener CreateStateless(ClusterConfiguration configuration, IServicePartition partition)
        {
            return new ServiceInstanceListener(context => new OrleansCommunicationListener(context, configuration, partition), OrleansServiceFabricEndpointName);
        }
    }
}