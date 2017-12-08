using System;
using System.Fabric;

namespace Orleans.Clustering.ServiceFabric.Models
{
    internal interface IResolvedServicePartition
    {
        /// <summary>
        /// Gets the identifier for this partition.
        /// </summary>
        /// <remarks>
        /// Note that the identifier may change for logically identical partitions if the service is redeployed.
        /// </remarks>
        Guid Id { get; }

        ServicePartitionKind Kind { get; }
        
        bool IsSamePartitionAs(IResolvedServicePartition other);
    }
}