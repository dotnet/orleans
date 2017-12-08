using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Query;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Services.Client;
using Orleans.Clustering.ServiceFabric.Models;

namespace Orleans.Clustering.ServiceFabric.Utilities
{
    internal class FabricQueryManager : IFabricQueryManager
    {
        private readonly ConcurrentDictionary<Uri, ConcurrentDictionary<ServicePartitionKey, ResolvedServicePartition>> previousResolves =
            new ConcurrentDictionary<Uri, ConcurrentDictionary<ServicePartitionKey, ResolvedServicePartition>>();
        private readonly FabricClient fabricClient;
        private readonly IServicePartitionResolver resolver;
        private readonly TimeSpan timeoutPerAttempt;
        private readonly TimeSpan maxBackoffInterval;

        public FabricQueryManager(
            FabricClient fabricClient,
            IServicePartitionResolver resolver)
        {
            this.fabricClient = fabricClient;
            this.resolver = resolver;
            this.timeoutPerAttempt = TimeSpan.FromSeconds(30);
            this.maxBackoffInterval = TimeSpan.FromSeconds(90);
        }

        /// <inheritdoc />
        public void UnregisterPartitionChangeHandler(long id)
        {
            this.fabricClient.ServiceManager.UnregisterServicePartitionResolutionChangeHandler(id);
        }

        /// <inheritdoc />
        public long RegisterPartitionChangeHandler(
            Uri serviceName,
            IResolvedServicePartition servicePartition,
            FabricPartitionResolutionChangeHandler handler)
        {
            var partition = servicePartition as ResolvedServicePartitionWrapper;
            if (partition == null)
            {
                throw new ArgumentException(
                    string.Format(
                        "Only partitions of type {0} are supported. Provided type {1} is not supported.",
                        nameof(ResolvedServicePartitionWrapper),
                        servicePartition.GetType()),
                    nameof(servicePartition));
            }
            
            // Wrap the provided handler so that it's compatible with Service Fabric.
            void ChangeHandler(FabricClient source, long id, ServicePartitionResolutionChange args)
            {
                ServicePartitionSilos result = null;
                if (!args.HasException)
                {
                    result = new ServicePartitionSilos(
                        new ResolvedServicePartitionWrapper(args.Result),
                        args.Result.GetPartitionEndpoints());
                }

                handler(id, new FabricPartitionResolutionChange(result, args.Exception));
            }

            var sm = this.fabricClient.ServiceManager;
            switch (servicePartition.Kind)
            {
                case ServicePartitionKind.Int64Range:
                    return sm.RegisterServicePartitionResolutionChangeHandler(
                        serviceName,
                        ((Int64RangePartitionInformation) partition.Partition.Info).LowKey,
                        ChangeHandler);
                case ServicePartitionKind.Named:
                    return sm.RegisterServicePartitionResolutionChangeHandler(
                        serviceName,
                        ((NamedPartitionInformation) partition.Partition.Info).Name,
                        ChangeHandler);
                case ServicePartitionKind.Singleton:
                    return sm.RegisterServicePartitionResolutionChangeHandler(serviceName, ChangeHandler);
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(servicePartition),
                        $"Partition kind {servicePartition.Kind} is not supported");
            }
        }

        /// <inheritdoc />
        public async Task<ServicePartitionSilos[]> ResolveSilos(Uri serviceName)
        {
            var fabricPartitions = await this.QueryServicePartitions(serviceName);
            var resolvedPartitions = new List<ServicePartitionSilos>(fabricPartitions.Count);
            foreach (var fabricPartition in fabricPartitions)
            {
                var partitionKey = fabricPartition.PartitionInformation.GetPartitionKey();
                var resolvedPartition = await this.ResolvePartition(serviceName, partitionKey, CancellationToken.None);
                resolvedPartitions.Add(resolvedPartition);
            }

            return resolvedPartitions.ToArray();
        }

        /// <inheritdoc />
        public async Task<ServicePartitionSilos> ResolvePartition(
            Uri serviceName,
            ServicePartitionKey partitionKey,
            CancellationToken cancellationToken)
        {
            ResolvedServicePartition result;
            var cache = this.previousResolves.GetOrAdd(serviceName, CreateCache);
            if (cache.TryGetValue(partitionKey, out var previousResult))
            {
                // Re-resolve the partition and avoid caching.
                result = await this.resolver.ResolveAsync(
                    previousResult,
                    this.timeoutPerAttempt,
                    this.maxBackoffInterval,
                    cancellationToken);
            }
            else
            {
                // Perform an initial resolution for the partition.
                result = await this.resolver.ResolveAsync(
                    serviceName,
                    partitionKey,
                    this.timeoutPerAttempt,
                    this.maxBackoffInterval,
                    cancellationToken);
            }

            // Cache the results of this resolution to provide to the next resolution call.
            cache.AddOrUpdate(
                partitionKey,
                _ => result,
                (key, existing) => existing.CompareVersion(result) < 0 ? result : existing);
            return new ServicePartitionSilos(
                new ResolvedServicePartitionWrapper(result),
                result.GetPartitionEndpoints());

            ConcurrentDictionary<ServicePartitionKey, ResolvedServicePartition> CreateCache(Uri uri)
            {
                return new ConcurrentDictionary<ServicePartitionKey, ResolvedServicePartition>(ServicePartitionKeyComparer.Instance);
            }
        }
        
        /// <summary>
        /// Returns the list of Service Fabric partitions for the given service.
        /// </summary>
        /// <returns>The list of Service Fabric partitions for the given service.</returns>
        private async Task<List<Partition>> QueryServicePartitions(Uri serviceName)
        {
            var partitions = new List<Partition>();
            var continuationToken = default(string);
            do
            {
                var batch = await this.fabricClient.QueryManager.GetPartitionListAsync(serviceName, continuationToken);
                if (batch.Count > 0) partitions.AddRange(batch);
                continuationToken = batch.ContinuationToken;
            } while (!string.IsNullOrWhiteSpace(continuationToken));
            partitions.Sort(
                (partition1, partition2) =>
                    partition1.PartitionInformation.Id.CompareTo(partition2.PartitionInformation.Id));
            return partitions;
        }

        private class ResolvedServicePartitionWrapper : IResolvedServicePartition
        {
            public ResolvedServicePartitionWrapper(ResolvedServicePartition partition)
            {
                this.Partition = partition;
            }

            public ResolvedServicePartition Partition { get; }

            public Guid Id => this.Partition.Info.Id;

            public ServicePartitionKind Kind => this.Partition.Info.Kind;

            public bool IsSamePartitionAs(IResolvedServicePartition other)
            {
                if (other is ResolvedServicePartitionWrapper otherWrapper)
                {
                    return this.Partition.IsSamePartitionAs(otherWrapper.Partition);
                }

                return false;
            }

            public override string ToString() => this.Partition.ToPartitionString();
        }

        /// <summary>
        /// Equality comparer for <see cref="ServicePartitionKey"/>.
        /// </summary>
        private struct ServicePartitionKeyComparer : IEqualityComparer<ServicePartitionKey>
        {
            /// <summary>
            /// Gets a singleton instance of this class.
            /// </summary>
            public static ServicePartitionKeyComparer Instance { get; } = new ServicePartitionKeyComparer();

            /// <inheritdoc />
            public bool Equals(ServicePartitionKey x, ServicePartitionKey y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (ReferenceEquals(x, null)) return false;
                if (ReferenceEquals(y, null)) return false;
                if (x.Kind != y.Kind) return false;
                switch (x.Kind)
                {
                    case ServicePartitionKind.Int64Range:
                        return (long) x.Value == (long) y.Value;
                    case ServicePartitionKind.Named:
                        return string.Equals(x.Value as string, y.Value as string, StringComparison.Ordinal);
                    case ServicePartitionKind.Singleton:
                        return true;
                    default:
                        ThrowKindOutOfRange(x);
                        return false;
                }
            }

            /// <inheritdoc />
            public int GetHashCode(ServicePartitionKey obj)
            {
                switch (obj.Kind)
                {
                    case ServicePartitionKind.Int64Range:
                        return ((long) obj.Value).GetHashCode();
                    case ServicePartitionKind.Named:
                        return ((string) obj.Value).GetHashCode();
                    case ServicePartitionKind.Singleton:
                        return 0;
                    default:
                        ThrowKindOutOfRange(obj);
                        return -1;
                }
            }

            private static void ThrowKindOutOfRange(ServicePartitionKey x)
            {
                throw new ArgumentOutOfRangeException(nameof(x), $"Partition kind {x.Kind} is not supported");

            }
        }
    }
}