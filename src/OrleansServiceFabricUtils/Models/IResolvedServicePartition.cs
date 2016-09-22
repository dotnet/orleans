using System;
using System.Fabric;

namespace Microsoft.Orleans.ServiceFabric.Models
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

        bool IsOlderThan(IResolvedServicePartition other);

        bool IsSamePartitionAs(IResolvedServicePartition other);
    }
}