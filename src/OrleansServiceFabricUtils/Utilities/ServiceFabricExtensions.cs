using System;
using System.Fabric;
using Microsoft.ServiceFabric.Services.Client;

namespace Microsoft.Orleans.ServiceFabric.Utilities
{
    internal static class ServiceFabricExtensions
    {
        /// <summary>
        /// Returns a value indicating whether or not <paramref name="left"/> is older than <paramref name="right"/>.
        /// </summary>
        /// <param name="left">One resolved partition.</param>
        /// <param name="right">The other resolved partition.</param>
        /// <returns>
        /// <see langword="true"/> if <paramref name="left"/> is older than <paramref name="right"/>, <see langword="false"/> otherwise.
        /// </returns>
        public static bool IsOlderThan(this ResolvedServicePartition left, ResolvedServicePartition right)
        {
            return left.Info.Id == right.Info.Id && left.CompareVersion(right) < 0;
        }

        /// <summary>
        /// Returns a value indicating whether or not <paramref name="left"/> is the same partition as <paramref name="right"/>.
        /// </summary>
        /// <param name="left">One resolved partition.</param>
        /// <param name="right">The other resolved partition.</param>
        /// <returns>
        /// <see langword="true"/> if <paramref name="left"/> is the same partition as <paramref name="right"/>, <see langword="false"/> otherwise.
        /// </returns>
        public static bool IsSamePartitionAs(this ResolvedServicePartition left, ResolvedServicePartition right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (ReferenceEquals(null, left) || ReferenceEquals(null, right))
            {
                return false;
            }

            return left.Info.IsSamePartitionAs(right.Info);
        }

        /// <summary>
        /// Returns a value indicating whether or not <paramref name="left"/> is the same partition as <paramref name="right"/>.
        /// </summary>
        /// <param name="left">One partition.</param>
        /// <param name="right">The other partition.</param>
        /// <returns>
        /// <see langword="true"/> if <paramref name="left"/> is the same partition as <paramref name="right"/>, <see langword="false"/> otherwise.
        /// </returns>
        public static bool IsSamePartitionAs(this ServicePartitionInformation left, ServicePartitionInformation right)
        {

            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (ReferenceEquals(null, left) || ReferenceEquals(null, right))
            {
                return false;
            }

            if (left.Kind != right.Kind)
            {
                return false;
            }

            switch (left.Kind)
            {
                case ServicePartitionKind.Singleton:
                    return true;
                case ServicePartitionKind.Int64Range:
                {
                    var actualLeft = (Int64RangePartitionInformation) left;
                    var actualRight = (Int64RangePartitionInformation) right;
                    return actualLeft.LowKey == actualRight.LowKey && actualLeft.HighKey == actualRight.HighKey;
                }
                case ServicePartitionKind.Named:
                {
                    var actualLeft = (NamedPartitionInformation) left;
                    var actualRight = (NamedPartitionInformation) right;
                    return string.Equals(actualLeft.Name, actualRight.Name, StringComparison.Ordinal);
                }
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(left),
                        $"Partition kind {left.Kind} is not supported");
            }
        }
        
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
        /// Returns a string representing the provided partition.
        /// </summary>
        /// <param name="partition">The partition.</param>
        /// <returns>A string representing the provided partition.</returns>
        public static string ToPartitionString(this ResolvedServicePartition partition) => partition?.Info.ToPartitionString();

        /// <summary>
        /// Returns a string representing the provided partition.
        /// </summary>
        /// <param name="partition">The partition.</param>
        /// <returns>A string representing the provided partition.</returns>
        public static string ToPartitionString(this ServicePartitionInformation partition)
        {
            switch (partition.Kind)
            {
                case ServicePartitionKind.Int64Range:
                    var id = (Int64RangePartitionInformation)partition;
                    return $"{id.LowKey:X}-{id.HighKey:X}/{partition.Id:N}";
                case ServicePartitionKind.Named:
                    return $"{((NamedPartitionInformation)partition).Name}/{partition.Id:N}";
                case ServicePartitionKind.Singleton:
                    return $"Singleton/{partition.Id:N}";
                default:
                    return $"{partition.Kind}/{partition.Id:N}";
            }
        }
    }
}