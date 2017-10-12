using System.Collections.Generic;

namespace Microsoft.Orleans.ServiceFabric.Models
{
    /// <summary>
    /// Represents a Service Fabric service partition and the Orleans silos within it.
    /// </summary>
    internal class ServicePartitionSilos
    {
        public ServicePartitionSilos(IResolvedServicePartition partition, List<FabricSiloInfo> silos)
        {
            this.Partition = partition;
            this.Silos = silos;
        }

        /// <summary>
        /// Gets the collection of silos in this partition.
        /// </summary>
        public List<FabricSiloInfo> Silos { get; }

        /// <summary>
        /// Gets the partition metadata.
        /// </summary>
        public IResolvedServicePartition Partition { get; }
    }
}