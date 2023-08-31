#nullable enable
using System;
using System.Collections.Generic;
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
                    GrainId = RowKeyToGrainId(RowKey),
                    SiloAddress = Runtime.SiloAddress.FromParsableString(SiloAddress),
                    ActivationId = Runtime.ActivationId.FromParsableString(ActivationId),
                    MembershipVersion = new MembershipVersion(MembershipVersion)
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
            tableDataManager = new AzureTableDataManager<GrainDirectoryEntity>(
                directoryOptions,
                loggerFactory.CreateLogger<AzureTableDataManager<GrainDirectoryEntity>>());
            clusterId = clusterOptions.Value.ClusterId;
        }

        public async Task<GrainAddress?> Lookup(GrainId grainId)
        {
            var result = await tableDataManager.ReadSingleTableEntryAsync(clusterId, GrainDirectoryEntity.GrainIdToRowKey(grainId));

            if (result.Entity is null)
            {
                return null;
            }

            return result.Item1.ToGrainAddress();
        }

        public Task<GrainAddress?> Register(GrainAddress address) => Register(address, null);

        public async Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousAddress)
        {
            (bool isSuccess, string eTag) result;
            if (previousAddress is not null)
            {
                var entry = GrainDirectoryEntity.FromGrainAddress(clusterId, address);
                var previousEntry = GrainDirectoryEntity.FromGrainAddress(clusterId, previousAddress);
                var (storedEntry, eTag) = await tableDataManager.ReadSingleTableEntryAsync(entry.PartitionKey, entry.RowKey);
                if (storedEntry is null)
                {
                    result = await tableDataManager.InsertTableEntryAsync(entry);
                }
                else if (storedEntry.ActivationId != previousEntry.ActivationId || storedEntry.SiloAddress != previousEntry.SiloAddress)
                {
                    return await Lookup(address.GrainId);
                }
                else
                {
                    _ = await tableDataManager.UpdateTableEntryAsync(entry, eTag);
                    return address;
                }
            }
            else
            {
                var entry = GrainDirectoryEntity.FromGrainAddress(clusterId, address);
                result = await tableDataManager.InsertTableEntryAsync(entry);
            }

            // Possible race condition?
            return result.isSuccess ? address : await Lookup(address.GrainId);
        }

        public async Task Unregister(GrainAddress address)
        {
            var result = await tableDataManager.ReadSingleTableEntryAsync(clusterId, GrainDirectoryEntity.GrainIdToRowKey(address.GrainId));

            // No entry found
            if (result.Entity is null)
            {
                return;
            }

            // Check if the entry in storage match the one we were asked to delete
            var entity = result.Item1;
            if (entity.ActivationId == address.ActivationId.ToParsableString())
                await tableDataManager.DeleteTableEntryAsync(GrainDirectoryEntity.FromGrainAddress(clusterId, address), entity.ETag.ToString());
        }

        public async Task UnregisterMany(List<GrainAddress> addresses)
        {
            if (addresses.Count <= tableDataManager.StoragePolicyOptions.MaxBulkUpdateRows)
            {
                await UnregisterManyBlock(addresses);
            }
            else
            {
                var tasks = new List<Task>();
                foreach (var subList in addresses.BatchIEnumerable(tableDataManager.StoragePolicyOptions.MaxBulkUpdateRows))
                {
                    tasks.Add(UnregisterManyBlock(subList));
                }
                await Task.WhenAll(tasks);
            }
        }

        public Task UnregisterSilos(List<SiloAddress> siloAddresses)
        {
            // Too costly to implement using Azure Table
            return Task.CompletedTask;
        }

        private async Task UnregisterManyBlock(List<GrainAddress> addresses)
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
            var entities = await tableDataManager.ReadTableEntriesAndEtagsAsync(queryBuilder.ToString());
            await tableDataManager.DeleteTableEntriesAsync(entities);
        }

        // Called by lifecycle, should not be called explicitely, except for tests
        public async Task InitializeIfNeeded(CancellationToken ct = default)
        {
            await tableDataManager.InitTableAsync();
        }

        public void Participate(ISiloLifecycle lifecycle)
        {

            lifecycle.Subscribe(nameof(AzureTableGrainDirectory), ServiceLifecycleStage.RuntimeInitialize, InitializeIfNeeded);
        }
    }
}
