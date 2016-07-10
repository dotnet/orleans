namespace Microsoft.Orleans.ServiceFabric
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Fabric;
    using System.Fabric.Query;
    using System.Threading;
    using System.Threading.Tasks;

    using global::Orleans.Runtime;

    using Microsoft.ServiceFabric.Services.Client;

    using Newtonsoft.Json;

    internal class PartitionResolver
    {
        private readonly FabricClient fabricClient;

        private readonly Uri serviceUri;

        private readonly ServicePartitionResolver resolver;

        private Task<SiloPartition[]> siloPartitions;

        private TaskCompletionSource<SiloPartition[]> refreshTask;

        private readonly LoggerImpl log;

        public PartitionResolver(FabricClient fabricClient, Uri serviceUri)
        {
            this.fabricClient = fabricClient;
            this.serviceUri = serviceUri;
            this.resolver = new ServicePartitionResolver(() => this.fabricClient);
            this.log = LogManager.GetLogger(nameof(PartitionResolver));
        }

        public Task<SiloPartition[]> GetPartitions()
        {
            var task = this.siloPartitions;
            if (task == null || task.IsFaulted)
            {
                task = this.siloPartitions ?? (this.siloPartitions = this.GetResolvedPartitions());
            }

            return task;
        }

        public void StartRefreshingPartitions()
        {
            var partitions = this.siloPartitions;
            var existing = this.refreshTask;
            if (existing == null || existing.Task.IsCompleted)
            {
                var replacement = new TaskCompletionSource<SiloPartition[]>();
                if (Interlocked.CompareExchange(ref this.refreshTask, replacement, existing) == replacement)
                {
                    this.RefreshPartitions(partitions).ContinueWith(antecedent => { this.siloPartitions = antecedent; });
                }
            }
        }

        private async Task<SiloPartition[]> RefreshPartitions(Task<SiloPartition[]> partitionsTask)
        {
            try
            {
                var partitions = await partitionsTask;
                for (var i = 0; i < partitions.Length; i++)
                {
                    var partition = partitions[i];
                    var resolved = await this.resolver.ResolveAsync(partition.Partition, CancellationToken.None);
                    partitions[i].Address = SiloAddress.FromParsableString(resolved.GetEndpoint().Address);
                    partitions[i].Partition = resolved;
                }

                return partitions;
            }
            catch (Exception exception)
            {
                this.log.Warn(exception.GetHashCode(), "Exception refreshing partitions.", exception);
                return await this.GetResolvedPartitions();
            }
        }

        private async Task<SiloPartition[]> GetResolvedPartitions()
        {
            var unresolvedPartitions = await this.GetAllPartitions();
            var resolvedPartitions = new List<SiloPartition>(unresolvedPartitions.Count);
            foreach (var partition in unresolvedPartitions)
            {
                var resolved = await this.resolver.ResolveAsync(this.serviceUri, this.GetPartitionKey(partition.PartitionInformation), CancellationToken.None);
                var endpointsJson = resolved.GetEndpoint().Address;
                var endpoints = JsonConvert.DeserializeObject<ServicePartitionEndpoints>(endpointsJson);
                resolvedPartitions.Add(new SiloPartition
                {
                    Address = SiloAddress.FromParsableString(endpoints.Endpoints[OrleansServiceListener.OrleansServiceFabricEndpointName]),
                    Partition = resolved
                });
            }
            return resolvedPartitions.ToArray();
        }

        private async Task<List<Partition>> GetAllPartitions()
        {
            var partitions = new List<Partition>();
            var continuationToken = default(string);
            do
            {
                var batch = await this.fabricClient.QueryManager.GetPartitionListAsync(this.serviceUri, continuationToken);
                if (batch.Count > 0) partitions.AddRange(batch);
                continuationToken = batch.ContinuationToken;
            }
            while (!string.IsNullOrWhiteSpace(continuationToken));
            partitions.Sort((partition1, partition2) => partition1.PartitionInformation.Id.CompareTo(partition2.PartitionInformation.Id));
            return partitions;
        }

        private ServicePartitionKey GetPartitionKey(ServicePartitionInformation partitionInformation)
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
                    throw new ArgumentOutOfRangeException(nameof(partitionInformation), $"Partition kind {partitionInformation.Kind} is not supported");
            }
        }

        internal struct SiloPartition
        {
            public SiloAddress Address { get; set; }

            public ResolvedServicePartition Partition { get; set; }
        }

        internal class ServicePartitionEndpoints
        {
            public Dictionary<string, string> Endpoints { get; set; }
        }
    }
}