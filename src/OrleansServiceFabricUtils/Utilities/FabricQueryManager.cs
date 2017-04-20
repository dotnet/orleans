using System.Collections.Generic;
using System.Threading;
using Microsoft.Orleans.ServiceFabric.Models;

namespace Microsoft.Orleans.ServiceFabric.Utilities
{
    using System;
    using System.Fabric;
    using System.Fabric.Query;
    using System.Threading.Tasks;

    using Microsoft.ServiceFabric.Services.Client;

    internal class FabricQueryManager : IFabricQueryManager
    {
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
            ServicePartitionResolutionChangeHandler actualHandler = (source, id, args) =>
            {
                ServicePartitionSilos result = null;
                if (!args.HasException)
                {
                    result = new ServicePartitionSilos(
                        new ResolvedServicePartitionWrapper(args.Result),
                        args.Result.GetPartitionEndpoints());
                }

                handler(
                    id,
                    new FabricPartitionResolutionChange(result, args.Exception));
            };

            var sm = this.fabricClient.ServiceManager;
            switch (servicePartition.Kind)
            {
                case ServicePartitionKind.Int64Range:
                    return sm.RegisterServicePartitionResolutionChangeHandler(
                        serviceName,
                        ((Int64RangePartitionInformation) partition.Partition.Info).LowKey,
                        actualHandler);
                case ServicePartitionKind.Named:
                    return sm.RegisterServicePartitionResolutionChangeHandler(
                        serviceName,
                        ((NamedPartitionInformation) partition.Partition.Info).Name,
                        actualHandler);
                case ServicePartitionKind.Singleton:
                    return sm.RegisterServicePartitionResolutionChangeHandler(serviceName, actualHandler);
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
            var resolved = await this.resolver.ResolveAsync(
                serviceName,
                partitionKey,
                this.timeoutPerAttempt,
                this.maxBackoffInterval,
                cancellationToken);

            return new ServicePartitionSilos(
                new ResolvedServicePartitionWrapper(resolved),
                resolved.GetPartitionEndpoints());
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

            public bool IsOlderThan(IResolvedServicePartition other)
            {
                var otherWrapper = other as ResolvedServicePartitionWrapper;
                if (otherWrapper == null) return false;

                return this.Partition.IsOlderThan(otherWrapper.Partition);
            }

            public bool IsSamePartitionAs(IResolvedServicePartition other)
            {
                var otherWrapper = other as ResolvedServicePartitionWrapper;
                if (otherWrapper == null) return false;

                return this.Partition.IsSamePartitionAs(otherWrapper.Partition);
            }

            public override string ToString() => this.Partition.ToPartitionString();
        }
    }
}