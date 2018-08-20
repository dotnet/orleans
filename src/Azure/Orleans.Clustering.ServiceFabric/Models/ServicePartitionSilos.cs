using System.Collections.Generic;
using System.Fabric;
using System.Linq;
using Orleans.Clustering.ServiceFabric.Utilities;
using Orleans.ServiceFabric;

namespace Orleans.Clustering.ServiceFabric.Models
{
    /// <summary>
    /// Represents a Service Fabric service partition and the Orleans silos within it.
    /// </summary>
    internal class ServicePartitionSilos
    {
        public ServicePartitionSilos(ResolvedServicePartition partition)
        {
            this.Partition = partition;
            this.Silos = partition.GetPartitionEndpoints();
        }

        /// <summary>
        /// Gets the collection of silos in this partition.
        /// </summary>
        public List<FabricSiloInfo> Silos { get; }

        /// <summary>
        /// Gets the partition metadata.
        /// </summary>
        public ResolvedServicePartition Partition { get; }

        public override string ToString()
        {
            return $"{nameof(Partition)}: {this.Partition}, {nameof(Silos)}: {string.Join(", ", this.Silos.Select(s => $"{{{s}}}"))}";
        }
    }
}