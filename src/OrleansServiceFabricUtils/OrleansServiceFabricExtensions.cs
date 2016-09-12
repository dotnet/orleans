namespace Microsoft.Orleans.ServiceFabric
{
    using System.Fabric;

    using global::Orleans;
    using global::Orleans.Runtime;

    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.DependencyInjection.Extensions;

    using StatefulService = Microsoft.ServiceFabric.Services.Runtime.StatefulService;

    public static class OrleansServiceFabricExtensions
    {

#warning DO NOT MERGE! See below comment!
        //Since Service Fabric can map multiple partitions to a given node (useful for overpartitioning) and since multiple partitions share the same process,
        // there is currently an error in placement code whereby a grain intended for partition A can be activated on a node which was formerly a primary for
        // partition A & B and is now only a primary for partition B.
        // The easiest fix is to remove all static instances so that each replica is an entirely different instance of Orleans even though they share the same process/appdomain.
        // Alternatively, per-replica appdomains can be used or another solution can be sought.
        // Note that SiloAddress is used to identify a partition & SiloAddress includes a uniquifier based on the local clock ticks. To ensure correctness, placement may need to use more info (silo name could contain replica id?).

        public static IServiceCollection AddServiceFabricSupport(this IServiceCollection serviceCollection, StatefulService service)
        {
            serviceCollection.TryAddSingleton<FabricClient>();

            // Use Serivce Fabric as the membership gateway.
            serviceCollection.AddSingleton<IMembershipTable, ServiceFabricNamingServiceGatewayProvider>();

            // Use Service Fabric as the placement director for the ConsistentPartition placement strategy.
            serviceCollection.AddSingleton<IPlacementDirector<ConsistentPartitionPlacement>, ServiceFabricPartitionPlacementDirector>();

            // In order to support local, replicated persistence, the state manager must be registered.
            serviceCollection.AddTransient(_ => service.StateManager);
            serviceCollection.AddTransient<ServiceContext>(_ => service.Context);

            return serviceCollection;
        }
    }
}