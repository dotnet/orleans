namespace Microsoft.Orleans.ServiceFabric
{
    using System;
    using System.Fabric;
    using System.Threading.Tasks;

    using global::Orleans.Runtime;
    using global::Orleans.Runtime.Placement;

    internal class ServiceFabricPartitionPlacementDirector : PlacementDirector, IPlacementDirector<ConsistentPartitionPlacement>, ISiloStatusListener
    {
        private readonly PartitionResolver partitionResolver;
        
        public ServiceFabricPartitionPlacementDirector(FabricClient fabricClient, ServiceContext serviceContext, IMembershipOracle membershipOracle)
        {
            this.partitionResolver = new PartitionResolver(fabricClient, serviceContext.ServiceName);
            membershipOracle.SubscribeToSiloStatusEvents(this);
        }

        private async Task<SiloAddress> GetPartition(GrainId target)
        {
            var partitions = await this.partitionResolver.GetPartitions();
            if (partitions.Length == 0) throw new InvalidOperationException("Attempted to map grain to partition, but there are no partitions.");
            
            var id = Math.Abs(target.GetHashCode()) % partitions.Length;
            return partitions[id].Address;
        }

        public override async Task<PlacementResult> OnSelectActivation(PlacementStrategy strategy, GrainId target, IPlacementContext context)
        {
            var places = (await context.Lookup(target)).Addresses;
            PlacementResult result = null;
            if (places.Count > 0)
            {
                var correctPartition = await this.GetPartition(target);
                foreach (var place in places)
                {
                    if (place.Silo.Equals(correctPartition))
                    {
                        result = PlacementResult.IdentifySelection(place);
                        break;
                    }
                }
            }
            return result;
        }

        public override async Task<PlacementResult> OnAddActivation(PlacementStrategy strategy, GrainId grain, IPlacementContext context)
        {
            var grainType = context.GetGrainTypeName(grain);
            var siloAddress = await this.GetPartition(grain);
            var result = PlacementResult.SpecifyCreation(siloAddress, strategy, grainType);
            return result;
        }

        public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
        {
            this.partitionResolver.StartRefreshingPartitions();
        }
    }
}
