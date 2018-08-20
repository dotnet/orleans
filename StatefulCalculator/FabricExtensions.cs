using System;
using System.Fabric;
using Microsoft.ServiceFabric.Services.Client;

namespace StatefulCalculator
{
    internal static class FabricExtensions
    {
        /// <summary>
        /// Returns the partition key for the provided partition.
        /// </summary>
        /// <param name="partitionInformation">The partition.</param>
        /// <returns>The partition key for the provided partition.</returns>
        public static ServicePartitionKey GetPartitionKey(this ServicePartitionInformation partitionInformation)
        {
            switch (partitionInformation.Kind)
            {
                case ServicePartitionKind.Int64Range:
                    return new ServicePartitionKey(((Int64RangePartitionInformation)partitionInformation).LowKey);
                case ServicePartitionKind.Named:
                    return new ServicePartitionKey(((NamedPartitionInformation)partitionInformation).Name);
                case ServicePartitionKind.Singleton:
                    return ServicePartitionKey.Singleton;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(partitionInformation),
                        $"Partition kind {partitionInformation.Kind} is not supported");
            }
        }

        /// <summary>
        /// Returns the partition key for the provided partition.
        /// </summary>
        /// <param name="partitionInformation">The partition.</param>
        /// <returns>The partition key for the provided partition.</returns>
        public static string GetPartitionKeyString(this ServicePartitionInformation partitionInformation)
        {
            var key = partitionInformation.GetPartitionKey();
            return $"{key.Kind}/{key.Value?.ToString() ?? "Singleton"}";
        }
    }
}