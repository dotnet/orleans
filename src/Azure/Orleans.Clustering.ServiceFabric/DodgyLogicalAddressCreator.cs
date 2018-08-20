using System;
using System.Fabric;
using System.Linq;
using System.Net;
using Orleans.Clustering.ServiceFabric.Models;
using Orleans.Runtime;

namespace Orleans.Clustering.ServiceFabric
{
    public static class DodgyLogicalAddressCreator
    {
        public static int ConvertToId(SiloAddress address)
        {
            var hashCode = BitConverter.ToInt32(address.Endpoint.Address.GetAddressBytes(), 0);
            return hashCode;
        }

        public static int ConvertToId(ResolvedServicePartition silos)
        {
            return silos.Info.Id.GetHashCode();
        }

        internal static SiloAddress ConvertToLogicalAddress(ServicePartitionSilos silos)
        {
            var hashCode = (uint)silos.Partition.Info.Id.GetHashCode();
            return SiloAddress.New(new IPEndPoint(hashCode, 65533), silos.Silos?.FirstOrDefault()?.SiloAddress.Generation ?? 0);
        }

        public static SiloAddress ConvertToLogicalAddress(ServicePartitionInformation partition, int generation)
        {
            var hashCode = (uint)partition.Id.GetHashCode();
            return SiloAddress.New(new IPEndPoint(hashCode, 65533), generation);
        }

        public static SiloAddress ConvertToLogicalAddress(Guid partitionId, int generation)
        {
            var hashCode = (uint)partitionId.GetHashCode();
            return SiloAddress.New(new IPEndPoint(hashCode, 65533), generation);
        }
    }
}