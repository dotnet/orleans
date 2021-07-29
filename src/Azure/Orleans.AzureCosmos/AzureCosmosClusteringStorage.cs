using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Orleans.AzureCosmos
{
    internal class AzureCosmosClusteringStorage : AzureCosmosStorage, IMembershipTable
    {
        protected readonly AzureCosmosClusteringOptions options;
        protected readonly string clusterId;
        protected readonly PartitionKey partitionKey;

        private const string VersionRow = "Version"; // Row key for version row.
        protected const string StatusActive = nameof(SiloStatus.Active);

        public AzureCosmosClusteringStorage(
            IOptions<AzureCosmosClusteringOptions> options,
            IOptions<ClusterOptions> clusterOptions,
            ILoggerFactory loggerFactory)
            : base(loggerFactory)
        {
            this.options = options.Value;
            this.clusterId = clusterOptions.Value.ClusterId;
            this.partitionKey = new(clusterId);
        }

        public async Task InitializeMembershipTable(bool tryInitTableVersion)
        {
            try
            {
                logger.Info("Initializing membership container for cluster id {0}", clusterId);
                await Init(options, new()
                {
                    PartitionKeyPath = "/" + nameof(SiloRecord.Cluster),
                    IndexingPolicy = new()
                    {
                        ExcludedPaths = { new() { Path = "/*" } },
                        IncludedPaths = {
                            new() { Path = "/" + nameof(SiloRecord.Status) + "/?" },
                            new() { Path = "/" + nameof(SiloRecord.ProxyPort) + "/?" },
                        }
                    }
                });

                // even if I am not the one who created the table,
                // try to insert an initial table version if it is not already there,
                // so we always have a first table version row, before this silo starts working.
                if (tryInitTableVersion)
                {
                    if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Reading VersionRow: PK={0} from Container={1}", clusterId, options.ContainerName);

                    bool create;
                    var startTime = DateTime.UtcNow;
                    using (var res = await container.ReadItemStreamAsync(VersionRow, partitionKey))
                    {
                        CheckAlertSlowAccess(startTime, "ReadItem");
                        if (!(create = res.StatusCode == HttpStatusCode.NotFound))
                            res.EnsureSuccessStatusCode();
                    }

                    if (create)
                    {
                        var payload = GetVersionRecord(0).Serialize();
                        if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Writing VersionRow: PK={0} to Container={1}", clusterId, options.ContainerName);

                        startTime = DateTime.UtcNow;
                        using var res = await container.CreateItemStreamAsync(payload, partitionKey, noContentResponse);
                        CheckAlertSlowAccess(startTime, "CreateItem");

                        if (res.StatusCode != HttpStatusCode.Conflict)
                        {
                            res.EnsureSuccessStatusCode();
                            logger.Info("Created new table version row.");
                        }
                    }
                }
            }
            catch (Exception ex) when (Log(ex)) { throw; }
        }

        public Task DeleteMembershipTableEntries(string clusterId) => throw new NotSupportedException();

        public async Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
        {
            try
            {
                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Reading defunct entries before {0} for cluster {1} from {2}", beforeDate, clusterId, options.ContainerName);

                var sql = new QueryDefinition($"SELECT VALUE c.id FROM c WHERE c._ts<@ts AND c.Status!='{StatusActive}'")
                    .WithParameter("@ts", beforeDate.ToUnixTimeSeconds());
                using var query = container.GetItemQueryIterator<string>(sql, null, requestOptions: new() { PartitionKey = partitionKey });

                var startTime = DateTime.UtcNow;
                var ls = new List<string>();
                while (query.HasMoreResults)
                    ls.AddRange(await query.ReadNextAsync());
                CheckAlertSlowAccess(startTime, "ReadItems");
                ls.Remove(VersionRow);

                if (ls.Count == 0) return;

                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Deleting {0} defunct entries for cluster {1} from {2}", ls.Count, clusterId, options.ContainerName);
                var results = await Task.WhenAll(ls.Select(id => container.DeleteItemStreamAsync(id, partitionKey)));
                try
                {
                    foreach (var r in results)
                        if (r.StatusCode != HttpStatusCode.NotFound) r.EnsureSuccessStatusCode();
                }
                finally
                {
                    foreach (var r in results) r.Dispose();
                }
            }
            catch (Exception ex) when (Log(ex)) { throw; }
        }

        public Task<MembershipTableData> ReadAll()
        {
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Reading all entries for cluster {0} from {1}", clusterId, options.ContainerName);
            return ReadRows();
        }

        public Task<MembershipTableData> ReadRow(SiloAddress key)
        {
            try
            {
                var rowKey = key.ToParsableString();
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Reading entry for {0} cluster {1} from {2}", rowKey, clusterId, options.ContainerName);
                var sql = new QueryDefinition($"SELECT * FROM c WHERE c.id='{VersionRow}' OR c.id=@id")
                    .WithParameter("@id", rowKey);
                return ReadRows(sql, rowKey);
            }
            catch (Exception ex) when (Log(ex)) { throw; }
        }

        private async Task<MembershipTableData> ReadRows(QueryDefinition sql = null, string key = null)
        {
            try
            {
                using var query = container.GetItemQueryStreamIterator(sql, null, requestOptions: new() { PartitionKey = partitionKey });
                var startTime = DateTime.UtcNow;
                var ls = new List<SiloRecord>();
                do
                {
                    // Task.Run is a workaround for https://github.com/Azure/azure-cosmos-dotnet-v2/issues/687
                    using var res = await Task.Run(() => query.ReadNextAsync());
                    res.EnsureSuccessStatusCode();
                    ls.AddRange(Deserialize<QueryResponse>(res).Documents);
                } while (query.HasMoreResults);
                CheckAlertSlowAccess(startTime, "ReadAll");

                var entries = new List<Tuple<MembershipEntry, string>>();
                TableVersion version = null;
                foreach (var record in ls)
                {
                    if (record.Id == VersionRow)
                        version = new(record.Version, record.ETag);
                    else
                        entries.Add(Tuple.Create(record.AsMembershipEntry(), record.ETag));
                }

                var data = new MembershipTableData(entries, version);
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    if (key is null) logger.Trace("ReadAll Table:\n{0}", data);
                    else logger.Trace("Read my entry {0} Table:\n{1}", key, data);
                }
                return data;
            }
            catch (Exception ex) when (Log(ex)) { throw; }
        }

        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("InsertRow {0}, table version={1}, etag={2}", entry, tableVersion, tableVersion.VersionEtag);
            return UpsertRow(entry, null, tableVersion);
        }

        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("UpdateRow {0}, etag={1}, table version={2}, etag={3}", entry, etag, tableVersion, tableVersion.VersionEtag);
            return UpsertRow(entry, etag, tableVersion);
        }

        private async Task<bool> UpsertRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            try
            {
                var record = AsSiloRecord(entry);
                var version = GetVersionRecord(tableVersion.Version);

                var batch = container.CreateTransactionalBatch(partitionKey);

                if (etag is null)
                {
                    batch.CreateItemStream(record.Serialize(), new() { EnableContentResponseOnWrite = false });
                }
                else
                {
                    batch.ReplaceItemStream(record.Id, record.Serialize(), new() { IfMatchEtag = etag, EnableContentResponseOnWrite = false });
                }

                batch.ReplaceItemStream(VersionRow, version.Serialize(), new() { IfMatchEtag = tableVersion.VersionEtag, EnableContentResponseOnWrite = false });

                var startTime = DateTime.UtcNow;
                // Task.Run is a workaround for https://github.com/Azure/azure-cosmos-dotnet-v2/issues/687
                using var res = await Task.Run(() => batch.ExecuteAsync());
                CheckAlertSlowAccess(startTime, "UpsertItem");

                if (res.IsSuccessStatusCode) return true;

                if (res.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
                {
                    logger.LogWarning($"{(etag is null ? "Insert" : "Update")} failed due to contention. Entry {entry}, etag={etag}, table version={tableVersion}, etag={tableVersion.VersionEtag}");
                    return false;
                }
                throw new CosmosException(res.ErrorMessage, res.StatusCode, 0, res.ActivityId, res.RequestCharge);
            }
            catch (Exception ex) when (Log(ex)) { throw; }
        }

        public async Task UpdateIAmAlive(MembershipEntry entry)
        {
            try
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Merge {0}", entry);
                var rowKey = entry.SiloAddress.ToParsableString();

retry:
                SiloRecord record;
                var startTime = DateTime.UtcNow;
                using (var res = await container.ReadItemStreamAsync(rowKey, partitionKey))
                {
                    CheckAlertSlowAccess(startTime, "ReadItem");
                    res.EnsureSuccessStatusCode();
                    record = Deserialize<SiloRecord>(res);
                }

                var etag = record.ETag;
                record.ETag = null;
                record.IAmAliveTime = entry.IAmAliveTime;
                var payload = record.Serialize();

                startTime = DateTime.UtcNow;
                using (var res = await container.ReplaceItemStreamAsync(payload, rowKey, partitionKey, requestOptions: new()
                {
                    IfMatchEtag = etag,
                    EnableContentResponseOnWrite = false
                }))
                {
                    CheckAlertSlowAccess(startTime, "ReplaceItem");

                    if (res.StatusCode == HttpStatusCode.PreconditionFailed)
                    {
                        logger.Info("Merge failed due to contention, retrying. RowKey={0}, etag={1}", rowKey, etag);
                        goto retry;
                    }
                    res.EnsureSuccessStatusCode();
                }
            }
            catch (Exception ex) when (Log(ex)) { throw; }
        }

        private SiloRecord GetVersionRecord(int version) => new() { Id = VersionRow, Cluster = clusterId, Version = version };

        private sealed class SiloRecord : RecordBase
        {
            public string Cluster { get; set; }

            public int Version { get; set; }

            [JsonConverter(typeof(JsonStringEnumConverter))]
            public SiloStatus Status { get; set; }

            public string HostName { get; set; }
            public int ProxyPort { get; set; }
            public string RoleName { get; set; }
            public string SiloName { get; set; }
            public int UpdateZone { get; set; }
            public int FaultZone { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime IAmAliveTime { get; set; }

            public List<SuspectingInfo> Suspecting { get; set; }

            public MembershipEntry AsMembershipEntry() => new()
            {
                SiloAddress = SiloAddress.FromParsableString(Id),
                Status = Status,
                HostName = HostName,
                ProxyPort = ProxyPort,
                RoleName = RoleName,
                SiloName = SiloName,
                UpdateZone = UpdateZone,
                FaultZone = FaultZone,
                StartTime = StartTime,
                IAmAliveTime = IAmAliveTime,
                SuspectTimes = Suspecting?.ConvertAll(s => Tuple.Create(SiloAddress.FromParsableString(s.Silo), s.Time))
            };
        }

        private struct SuspectingInfo
        {
            public string Silo { get; set; }
            public DateTime Time { get; set; }
        }

        private sealed class QueryResponse
        {
            public SiloRecord[] Documents { get; set; }
        }

        private SiloRecord AsSiloRecord(MembershipEntry r) => new()
        {
            Id = r.SiloAddress.ToParsableString(),
            Cluster = clusterId,
            Status = r.Status,
            HostName = r.HostName,
            ProxyPort = r.ProxyPort,
            RoleName = r.RoleName,
            SiloName = r.SiloName,
            UpdateZone = r.UpdateZone,
            FaultZone = r.FaultZone,
            StartTime = r.StartTime,
            IAmAliveTime = r.IAmAliveTime,
            Suspecting = r.SuspectTimes?.Count > 0 ? r.SuspectTimes.ConvertAll(s => new SuspectingInfo { Silo = s.Item1.ToParsableString(), Time = s.Item2 }) : null
        };
    }

    internal sealed class AzureCosmosGatewayStorage : AzureCosmosClusteringStorage, IGatewayListProvider
    {
        public AzureCosmosGatewayStorage(
            IOptions<AzureCosmosClusteringOptions> options,
            IOptions<ClusterOptions> clusterOptions,
            IOptions<GatewayOptions> gatewayOptions,
            ILoggerFactory loggerFactory)
            : base(options, clusterOptions, loggerFactory)
            => MaxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;

        public TimeSpan MaxStaleness { get; }
        public bool IsUpdatable => true;

        public Task InitializeGatewayListProvider() => InitializeMembershipTable(false);

        public async Task<IList<Uri>> GetGateways()
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Reading active gateway silos for cluster {0} from {1}.", clusterId, options.ContainerName);

            var sql = $"SELECT c.id,c.ProxyPort FROM c WHERE c.Status='{StatusActive}' AND c.ProxyPort>0";
            using var query = container.GetItemQueryStreamIterator(sql, null, requestOptions: new() { PartitionKey = partitionKey });
            var startTime = DateTime.UtcNow;
            var ls = new List<SiloQueryItem>();
            do
            {
                using var res = await query.ReadNextAsync();
                res.EnsureSuccessStatusCode();
                ls.AddRange(Deserialize<QueryResponse>(res).Documents);
            } while (query.HasMoreResults);
            CheckAlertSlowAccess(startTime, "ReadAll");

            logger.Info("Found {0} active gateways for cluster {1}.", ls.Count, clusterId);
            return ls.ConvertAll(r =>
            {
                var addr = SiloAddress.FromParsableString(r.Id);
                return new IPEndPoint(addr.Endpoint.Address, r.ProxyPort).ToGatewayUri();
            });
        }

        private sealed class SiloQueryItem
        {
            [JsonPropertyName("id")]
            public string Id { get; set; }
            public int ProxyPort { get; set; }
        }

        private sealed class QueryResponse
        {
            public SiloQueryItem[] Documents { get; set; }
        }
    }
}
