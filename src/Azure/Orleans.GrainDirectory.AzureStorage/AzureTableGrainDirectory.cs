#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.GrainDirectory.AzureStorage
{
    public class AzureTableGrainDirectory : IGrainDirectory, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly AzureTableDataManager<GrainDirectoryEntity> tableDataManager;
        private readonly string clusterId;

        internal class GrainDirectoryEntity : ITableEntity
        {
            public required string SiloAddress { get; set; }
            public required string ActivationId { get; set; }
            public required long MembershipVersion { get; set; }
            public required string PartitionKey { get; set; }
            public required string RowKey { get; set; }
            public DateTimeOffset? Timestamp { get; set; }
            public ETag ETag { get; set; }

            public GrainAddress ToGrainAddress()
            {
                return new GrainAddress
                {
                    GrainId = RowKeyToGrainId(this.RowKey),
                    SiloAddress = Runtime.SiloAddress.FromParsableString(this.SiloAddress),
                    ActivationId = Runtime.ActivationId.FromParsableString(this.ActivationId),
                    MembershipVersion = new MembershipVersion(this.MembershipVersion)
                };
            }

            public static GrainDirectoryEntity FromGrainAddress(string clusterId, GrainAddress address)
            {
                ArgumentNullException.ThrowIfNull(address.SiloAddress);

                return new GrainDirectoryEntity
                {
                    PartitionKey = clusterId,
                    RowKey = GrainIdToRowKey(address.GrainId),
                    SiloAddress = address.SiloAddress.ToParsableString(),
                    ActivationId = address.ActivationId.ToParsableString(),
                    MembershipVersion = address.MembershipVersion.Value,
                };
            }

            internal static string GrainIdToRowKey(GrainId grainId) => HttpUtility.UrlEncode(grainId.ToString(), Encoding.UTF8);

            internal static GrainId RowKeyToGrainId(string rowKey) => GrainId.Parse(HttpUtility.UrlDecode(rowKey, Encoding.UTF8));
        }

        public AzureTableGrainDirectory(
            AzureTableGrainDirectoryOptions directoryOptions,
            IOptions<ClusterOptions> clusterOptions,
            ILoggerFactory loggerFactory)
        {
            this.tableDataManager = new AzureTableDataManager<GrainDirectoryEntity>(
                directoryOptions,
                loggerFactory.CreateLogger<AzureTableDataManager<GrainDirectoryEntity>>());
            this.clusterId = clusterOptions.Value.ClusterId;
        }

        public async Task<GrainAddress?> Lookup(GrainId grainId, CancellationToken cancellationToken)
        {
            var result = await this.tableDataManager.ReadSingleTableEntryAsync(this.clusterId, GrainDirectoryEntity.GrainIdToRowKey(grainId), cancellationToken);

            if (result.Entity is null)
            {
                return null;
            }

            return result.Item1.ToGrainAddress();
        }

        public Task<GrainAddress?> Register(GrainAddress address, CancellationToken cancellationToken) => Register(address, null, cancellationToken);

        public async Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousAddress, CancellationToken cancellationToken)
        {
            (bool isSuccess, string eTag) result;
            if (previousAddress is not null)
            {
                var entry = GrainDirectoryEntity.FromGrainAddress(this.clusterId, address);
                var previousEntry = GrainDirectoryEntity.FromGrainAddress(this.clusterId, previousAddress);
                var (storedEntry, eTag) = await tableDataManager.ReadSingleTableEntryAsync(entry.PartitionKey, entry.RowKey, cancellationToken);
                if (storedEntry is null)
                {
                    result = await this.tableDataManager.InsertTableEntryAsync(entry, cancellationToken);
                }
                else if (storedEntry.ActivationId != previousEntry.ActivationId || storedEntry.SiloAddress != previousEntry.SiloAddress)
                {
                    return await Lookup(address.GrainId, cancellationToken);
                }
                else
                {
                    _ = await tableDataManager.UpdateTableEntryAsync(entry, eTag, cancellationToken);
                    return address;
                }
            }
            else
            {
                var entry = GrainDirectoryEntity.FromGrainAddress(this.clusterId, address);
                result = await this.tableDataManager.InsertTableEntryAsync(entry, cancellationToken);
            }

            // Possible race condition?
            return result.isSuccess ? address : await Lookup(address.GrainId, cancellationToken);
        }

        public async Task Unregister(GrainAddress address, CancellationToken cancellationToken)
        {
            var result = await this.tableDataManager.ReadSingleTableEntryAsync(this.clusterId, GrainDirectoryEntity.GrainIdToRowKey(address.GrainId), cancellationToken);

            // No entry found
            if (result.Entity is null)
            {
                return;
            }

            // Check if the entry in storage match the one we were asked to delete
            var entity = result.Item1;
            if (entity.ActivationId == address.ActivationId.ToParsableString())
                await this.tableDataManager.DeleteTableEntryAsync(GrainDirectoryEntity.FromGrainAddress(this.clusterId, address), entity.ETag.ToString(), cancellationToken);
        }

        public async Task UnregisterMany(List<GrainAddress> addresses, CancellationToken cancellationToken)
        {
            if (addresses.Count <= this.tableDataManager.StoragePolicyOptions.MaxBulkUpdateRows)
            {
                await UnregisterManyBlock(addresses, cancellationToken);
            }
            else
            {
                var tasks = new List<Task>();
                foreach (var subList in addresses.BatchIEnumerable(this.tableDataManager.StoragePolicyOptions.MaxBulkUpdateRows))
                {
                    tasks.Add(UnregisterManyBlock(subList, cancellationToken));
                }
                await Task.WhenAll(tasks);
            }
        }

        public Task UnregisterSilos(List<SiloAddress> siloAddresses, CancellationToken cancellationToken)
        {
            // Too costly to implement using Azure Table
            return Task.CompletedTask;
        }

        private async Task UnregisterManyBlock(List<GrainAddress> addresses, CancellationToken cancellationToken)
        {
            var queryBuilder = new StringBuilder();
            queryBuilder.Append(TableClient.CreateQueryFilter($"(PartitionKey eq {clusterId}) and ("));
            var first = true;
            foreach (var addr in addresses)
            {
                if (!first)
                {
                    queryBuilder.Append(" or ");
                }
                else
                {
                    first = false;
                }

                var rowKey = GrainDirectoryEntity.GrainIdToRowKey(addr.GrainId);
                var queryClause = TableClient.CreateQueryFilter($"((RowKey eq {rowKey}) and (ActivationId eq {addr.ActivationId.ToString()}))");
                queryBuilder.Append(queryClause);
            }

            queryBuilder.Append(')');
            var entities = await this.tableDataManager.ReadTableEntriesAndEtagsAsync(queryBuilder.ToString(), cancellationToken);
            await this.tableDataManager.DeleteTableEntriesAsync(entities, cancellationToken);
        }

        // Called by lifecycle, should not be called explicitely, except for tests
        public async Task InitializeIfNeeded(CancellationToken ct = default)
        {
            await this.tableDataManager.InitTableAsync(ct);
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(nameof(AzureTableGrainDirectory), ServiceLifecycleStage.RuntimeInitialize, InitializeIfNeeded);
        }

        public Task<GrainAddress?> Register(GrainAddress address) => Register(address, CancellationToken.None);
        public Task Unregister(GrainAddress address) => Unregister(address, CancellationToken.None);
        public Task<GrainAddress?> Lookup(GrainId grainId) => Lookup(grainId, CancellationToken.None);
        public Task UnregisterSilos(List<SiloAddress> siloAddresses) => UnregisterSilos(siloAddresses, CancellationToken.None);
    }
}
