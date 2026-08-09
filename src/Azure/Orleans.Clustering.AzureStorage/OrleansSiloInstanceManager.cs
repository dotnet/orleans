using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Orleans.Clustering.AzureStorage;
using Orleans.Clustering.AzureStorage.Utilities;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.AzureUtils
{
    internal partial class OrleansSiloInstanceManager
    {
        internal const int MaxMembershipSnapshotAttempts = 5;

        public string TableName { get; }

        private const string INSTANCE_STATUS_CREATED = nameof(SiloStatus.Created);  //"Created";
        private const string INSTANCE_STATUS_ACTIVE = nameof(SiloStatus.Active);    //"Active";
        private const string INSTANCE_STATUS_DEAD = nameof(SiloStatus.Dead);        //"Dead";

        private readonly AzureTableDataManager<SiloInstanceTableEntry> storage;
        private readonly IMembershipTableReadStorage membershipTableReadStorage;
        private readonly ILogger logger;
        private readonly AzureStoragePolicyOptions storagePolicyOptions;

        public string DeploymentId { get; private set; }

        private OrleansSiloInstanceManager(
            string clusterId,
            ILoggerFactory loggerFactory,
            AzureStorageOperationOptions options)
            : this(clusterId, loggerFactory, options, null)
        {
        }

        internal OrleansSiloInstanceManager(
            string clusterId,
            ILoggerFactory loggerFactory,
            AzureStorageOperationOptions options,
            IMembershipTableReadStorage? membershipTableReadStorage)
        {
            DeploymentId = clusterId;
            TableName = options.TableName;
            logger = loggerFactory.CreateLogger<OrleansSiloInstanceManager>();
            storage = new AzureTableDataManager<SiloInstanceTableEntry>(
                options,
                loggerFactory.CreateLogger<AzureTableDataManager<SiloInstanceTableEntry>>());
            this.membershipTableReadStorage = membershipTableReadStorage ?? new AzureMembershipTableReadStorage(storage);
            this.storagePolicyOptions = options.StoragePolicyOptions;
        }

        public static async Task<OrleansSiloInstanceManager> GetManager(
            string clusterId,
            ILoggerFactory loggerFactory,
            AzureStorageOperationOptions options)
        {
            var instance = new OrleansSiloInstanceManager(clusterId, loggerFactory, options);
            try
            {
                await instance.storage.InitTableAsync();
            }
            catch (Exception ex)
            {
                instance.LogErrorConnectingToAzureTable(ex, instance.storage.TableName);
                throw;
            }
            return instance;
        }

        public SiloInstanceTableEntry CreateTableVersionEntry(int tableVersion)
        {
            return CreateTableVersionEntry(
                SiloInstanceTableEntry.TABLE_VERSION_ROW,
                tableVersion.ToString(CultureInfo.InvariantCulture));
        }

        private SiloInstanceTableEntry CreateTableVersionEntry(string rowKey, string membershipVersion)
        {
            return new()
            {
                DeploymentId = DeploymentId,
                PartitionKey = DeploymentId,
                RowKey = rowKey,
                MembershipVersion = membershipVersion
            };
        }

        private (SiloInstanceTableEntry Min, SiloInstanceTableEntry Max) CreateBoundaryVersionEntries(
            SiloInstanceTableEntry tableVersionEntry)
        {
            var membershipVersion = tableVersionEntry.MembershipVersion
                ?? throw new ArgumentException("The table version entry must have a membership version.", nameof(tableVersionEntry));
            var min = CreateTableVersionEntry(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, membershipVersion);
            var max = CreateTableVersionEntry(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, membershipVersion);

            // Prevent older cleanup agents from treating boundary rows as defunct silo entries.
            min.Status = INSTANCE_STATUS_ACTIVE;
            max.Status = INSTANCE_STATUS_ACTIVE;
            return (min, max);
        }

        public void RegisterSiloInstance(SiloInstanceTableEntry entry)
        {
            entry.Status = INSTANCE_STATUS_CREATED;
            LogRegisterSiloInstance(entry);
            Task.WaitAll(new Task[] { storage.UpsertTableEntryAsync(entry) });
        }

        public Task<string> UnregisterSiloInstance(SiloInstanceTableEntry entry)
        {
            entry.Status = INSTANCE_STATUS_DEAD;
            LogUnregisterSiloInstance(entry);
            return storage.UpsertTableEntryAsync(entry);
        }

        public Task<string> ActivateSiloInstance(SiloInstanceTableEntry entry)
        {
            LogActivateSiloInstance(entry);
            entry.Status = INSTANCE_STATUS_ACTIVE;
            return storage.UpsertTableEntryAsync(entry);
        }

        /// <summary>
        /// Represent a silo instance entry in the gateway URI format.
        /// </summary>
        /// <param name="gateway">The input silo instance</param>
        /// <returns></returns>
        private static Uri ConvertToGatewayUri(SiloInstanceTableEntry gateway)
        {
            int proxyPort = 0;
            if (!string.IsNullOrEmpty(gateway.ProxyPort))
                int.TryParse(gateway.ProxyPort, out proxyPort);

            int gen = 0;
            if (!string.IsNullOrEmpty(gateway.Generation))
                int.TryParse(gateway.Generation, out gen);

            SiloAddress address = SiloAddress.New(IPAddress.Parse(gateway.Address!), proxyPort, gen);
            return address.ToGatewayUri();
        }

        public async Task<IList<Uri>> FindAllGatewayProxyEndpoints()
        {
            LogDebugSearchingGateway(this.DeploymentId);

            try
            {
                const string Zero = "0";
                var queryResults = await storage.ReadTableEntriesAndEtagsAsync(TableClient.CreateQueryFilter($"PartitionKey eq {DeploymentId} and Status eq {INSTANCE_STATUS_ACTIVE} and ProxyPort ne {Zero}"));

                var gatewaySiloInstances = queryResults.Select(entity => ConvertToGatewayUri(entity.Item1)).ToList();

                LogFoundGateway(gatewaySiloInstances.Count, this.DeploymentId);
                return gatewaySiloInstances;
            }catch(Exception exc)
            {
                LogErrorSearchingGateway(exc, this.DeploymentId);
                throw;
            }
        }

        public async Task<string> DumpSiloInstanceTable()
        {
            var queryResults = await storage.ReadAllTableEntriesForPartitionAsync(this.DeploymentId);

            SiloInstanceTableEntry[] entries = queryResults.Select(entry => entry.Item1).ToArray();

            var sb = new StringBuilder();
            sb.Append(string.Format("Deployment {0}. Silos: ", DeploymentId));

            // Loop through the results, displaying information about the entity
            Array.Sort(entries,
                (e1, e2) =>
                {
                    if (e1 == null) return (e2 == null) ? 0 : -1;
                    if (e2 == null) return (e1 == null) ? 0 : 1;
                    if (e1.SiloName == null) return (e2.SiloName == null) ? 0 : -1;
                    if (e2.SiloName == null) return (e1.SiloName == null) ? 0 : 1;
                    return string.CompareOrdinal(e1.SiloName, e2.SiloName);
                });
            foreach (SiloInstanceTableEntry entry in entries)
            {
                sb.AppendLine(string.Format("[IP {0}:{1}:{2}, {3}, Instance={4}, Status={5}]", entry.Address, entry.Port, entry.Generation,
                    entry.HostName, entry.SiloName, entry.Status));
            }
            return sb.ToString();
        }

        internal Task<string> MergeTableEntryAsync(SiloInstanceTableEntry data)
        {
            return storage.MergeTableEntryAsync(data, AzureTableUtils.ANY_ETAG); // we merge this without checking eTags.
        }

        internal Task<(SiloInstanceTableEntry? Entity, string? ETag)> ReadSingleTableEntryAsync(string partitionKey, string rowKey)
        {
            return storage.ReadSingleTableEntryAsync(partitionKey, rowKey);
        }

        internal async Task<int> DeleteTableEntries(string clusterId)
        {
            if (clusterId == null) throw new ArgumentNullException(nameof(clusterId));

            var entries = await storage.ReadAllTableEntriesForPartitionAsync(clusterId);

            await DeleteEntriesBatch(entries);

            return entries.Count;
        }

        public async Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
        {
            var entriesList = (await FindAllSiloEntries())
                .Where(entry => !SiloInstanceTableEntry.IsVersionRow(entry.Entity.RowKey)
                    && entry.Item1.Status != INSTANCE_STATUS_ACTIVE
                    && entry.Item1.Timestamp < beforeDate)
                .ToList();

            // Defunct-row cleanup intentionally does not advance the membership snapshot fence.
            await DeleteEntriesBatch(entriesList);
        }

        private async Task DeleteEntriesBatch(List<(SiloInstanceTableEntry, string)> entriesList)
        {
            if (entriesList.Count <= this.storagePolicyOptions.MaxBulkUpdateRows)
            {
                await storage.DeleteTableEntriesAsync(entriesList);
            }
            else
            {
                var tasks = new List<Task>();
                foreach (var batch in entriesList.BatchIEnumerable(this.storagePolicyOptions.MaxBulkUpdateRows))
                {
                    tasks.Add(storage.DeleteTableEntriesAsync(batch));
                }
                await Task.WhenAll(tasks);
            }
        }

        internal async Task<List<(SiloInstanceTableEntry, string)>> FindSiloEntryAndTableVersionRow(SiloAddress siloAddress)
        {
            string rowKey = SiloInstanceTableEntry.ConstructRowKey(siloAddress);

            var filter = TableClient.CreateQueryFilter($"(PartitionKey eq {DeploymentId}) and ((RowKey eq {rowKey}) or (RowKey eq {SiloInstanceTableEntry.TABLE_VERSION_ROW}))");
            var queryResults = await storage.ReadTableEntriesAndEtagsAsync(filter);
            if (queryResults.Count < 1 || queryResults.Count > 2)
                throw new KeyNotFoundException(string.Format("Could not find table version row or found too many entries. Was looking for key {0}, found = {1}", siloAddress, Utils.EnumerableToString(queryResults)));

            var numTableVersionRows = 0;
            foreach (var entry in queryResults)
            {
                if (entry.Item1.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW)
                {
                    numTableVersionRows++;
                }
            }

            if (numTableVersionRows < 1)
                throw new KeyNotFoundException(string.Format("Did not read table version row. Read = {0}", Utils.EnumerableToString(queryResults)));

            if (numTableVersionRows > 1)
                throw new KeyNotFoundException(string.Format("Read {0} table version rows, while was expecting only 1. Read = {1}", numTableVersionRows, Utils.EnumerableToString(queryResults)));

            return queryResults;
        }

        internal async Task<List<(SiloInstanceTableEntry Entity, string ETag)>> FindAllSiloEntries(
            CancellationToken cancellationToken = default)
        {
            for (var attempt = 1; ; attempt++)
            {
                var query = await membershipTableReadStorage.ReadAllTableEntriesForPartitionAsync(DeploymentId, cancellationToken);
                var tableVersion = ValidateAllSiloEntries(query.Entries);
                if (CanAcceptSnapshot(query, tableVersion, attempt))
                {
                    return RemoveBoundaryVersionRows(query.Entries);
                }
            }
        }

        private bool CanAcceptSnapshot(
            MembershipTableQueryResult query,
            string? tableVersion,
            int attempt)
        {
            if (!query.IsPaginated)
            {
                return true;
            }

            // Boundary-aware membership updates write both rows in the same transaction. Matching
            // values prove that none committed while the paginated query was being read.
            var first = query.Entries[0].Entity;
            var last = query.Entries[^1].Entity;
            var beforeVersion = first.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN
                ? first.MembershipVersion
                : null;
            var afterVersion = last.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX
                ? last.MembershipVersion
                : null;
            if (beforeVersion is null && afterVersion is null)
            {
                return true;
            }

            if (beforeVersion is not null
                && afterVersion is not null
                && (string.Equals(beforeVersion, afterVersion, StringComparison.Ordinal)
                    || IsLegacyTableVersionAhead(tableVersion, beforeVersion, afterVersion)))
            {
                return true;
            }

            if (attempt >= MaxMembershipSnapshotAttempts)
            {
                throw new InconsistentStateException(
                    $"Unable to read a consistent membership snapshot for cluster '{DeploymentId}' from table '{TableName}' after {MaxMembershipSnapshotAttempts} attempts.",
                    beforeVersion,
                    afterVersion);
            }

            return false;
        }

        private static bool IsLegacyTableVersionAhead(
            string? tableVersion,
            string beforeVersion,
            string afterVersion)
        {
            // Older silos only advance VersionRow. During a rolling upgrade, prefer availability
            // over snapshot consistency when that row proves that the boundary fence is stale.
            return int.TryParse(tableVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTableVersion)
                && int.TryParse(beforeVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBeforeVersion)
                && int.TryParse(afterVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedAfterVersion)
                && parsedTableVersion > parsedBeforeVersion
                && parsedTableVersion > parsedAfterVersion;
        }

        private static List<(SiloInstanceTableEntry Entity, string ETag)> RemoveBoundaryVersionRows(
            List<(SiloInstanceTableEntry Entity, string ETag)> queryResults)
        {
            if (queryResults.Count > 0
                && queryResults[^1].Entity.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX)
            {
                queryResults.RemoveAt(queryResults.Count - 1);
            }

            if (queryResults.Count > 0
                && queryResults[0].Entity.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN)
            {
                queryResults.RemoveAt(0);
            }

            return queryResults;
        }

        private static string? ValidateAllSiloEntries(List<(SiloInstanceTableEntry Entity, string ETag)> queryResults)
        {
            if (queryResults.Count < 1)
                throw new KeyNotFoundException(string.Format("Could not find enough rows in the FindAllSiloEntries call. Found = {0}", Utils.EnumerableToString(queryResults)));

            var numTableVersionRows = 0;
            string? tableVersion = null;
            foreach (var entry in queryResults)
            {
                if (entry.Entity.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW)
                {
                    numTableVersionRows++;
                    tableVersion = entry.Entity.MembershipVersion;
                }
            }

            if (numTableVersionRows < 1)
                throw new KeyNotFoundException(string.Format("Did not find table version row. Read = {0}", Utils.EnumerableToString(queryResults)));
            if (numTableVersionRows > 1)
                throw new KeyNotFoundException(string.Format("Read {0} table version rows, while was expecting only 1. Read = {1}", numTableVersionRows, Utils.EnumerableToString(queryResults)));

            return tableVersion;
        }

        /// <summary>
        /// Insert (create new) row entry
        /// </summary>
        internal async Task<bool> TryCreateTableVersionEntryAsync()
        {
            try
            {
                var versionRow = await storage.ReadSingleTableEntryAsync(DeploymentId, SiloInstanceTableEntry.TABLE_VERSION_ROW);
                if (versionRow.Entity != null)
                {
                    return false;
                }

                var entry = CreateTableVersionEntry(0);
                var boundaryEntries = CreateBoundaryVersionEntries(entry);
                await storage.CreateTableEntriesAsync([entry, boundaryEntries.Min, boundaryEntries.Max]);
                return true;
            }
            catch (Exception exc)
            {
                if (!AzureTableUtils.EvaluateException(exc, out var httpStatusCode, out var restStatus)) throw;

                LogTraceInsertSiloEntryConditionallyFailed(httpStatusCode, restStatus);
                if (AzureTableUtils.IsContentionError(httpStatusCode)) return false;

                throw;
            }
        }

        /// <summary>
        /// Insert (create new) row entry
        /// </summary>
        /// <param name="siloEntry">Silo Entry to be written</param>
        /// <param name="tableVersionEntry">Version row to update</param>
        /// <param name="tableVersionEtag">Version row eTag</param>
        internal async Task<bool> InsertSiloEntryConditionally(SiloInstanceTableEntry siloEntry, SiloInstanceTableEntry tableVersionEntry, string tableVersionEtag)
        {
            try
            {
                var boundaryEntries = CreateBoundaryVersionEntries(tableVersionEntry);
                await storage.CreateAndUpdateTableEntriesAsync(
                    siloEntry,
                    (tableVersionEntry, tableVersionEtag),
                    (boundaryEntries.Min, boundaryEntries.Max));
                return true;
            }
            catch (Exception exc)
            {
                if (!AzureTableUtils.EvaluateException(exc, out var httpStatusCode, out var restStatus)) throw;

                LogTraceInsertSiloEntryConditionallyFailed(httpStatusCode, restStatus);
                if (AzureTableUtils.IsContentionError(httpStatusCode)) return false;

                throw;
            }
        }

        /// <summary>
        /// Conditionally update the row for this entry, but only if the eTag matches with the current record in data store
        /// </summary>
        /// <param name="siloEntry">Silo Entry to be written</param>
        /// <param name="entryEtag">ETag value for the entry being updated</param>
        /// <param name="tableVersionEntry">Version row to update</param>
        /// <param name="versionEtag">ETag value for the version row</param>
        /// <returns></returns>
        internal async Task<bool> UpdateSiloEntryConditionally(SiloInstanceTableEntry siloEntry, string entryEtag, SiloInstanceTableEntry tableVersionEntry, string versionEtag)
        {
            try
            {
                var boundaryEntries = CreateBoundaryVersionEntries(tableVersionEntry);
                await storage.UpdateTableEntriesAsync(
                    (siloEntry, entryEtag),
                    (tableVersionEntry, versionEtag),
                    (boundaryEntries.Min, boundaryEntries.Max));
                return true;
            }
            catch (Exception exc)
            {
                if (!AzureTableUtils.EvaluateException(exc, out var httpStatusCode, out var restStatus)) throw;

                LogTraceUpdateSiloEntryConditionallyFailed(httpStatusCode, restStatus);
                if (AzureTableUtils.IsContentionError(httpStatusCode)) return false;

                throw;
            }
        }

        [LoggerMessage(
            EventId = (int)TableStorageErrorCode.AzureTable_33,
            Level = LogLevel.Error,
            Message = "Exception trying to create or connect to the Azure table {TableName}"
        )]
        private partial void LogErrorConnectingToAzureTable(Exception exception, string tableName);

        [LoggerMessage(
            EventId = (int)ErrorCode.Runtime_Error_100270,
            Level = LogLevel.Information,
            Message = "Registering silo instance: {Data}"
        )]
        private partial void LogRegisterSiloInstance(SiloInstanceTableEntry data);

        [LoggerMessage(
            EventId = (int)ErrorCode.Runtime_Error_100271,
            Level = LogLevel.Information,
            Message = "Unregistering silo instance: {Data}"
        )]
        private partial void LogUnregisterSiloInstance(SiloInstanceTableEntry data);

        [LoggerMessage(
            EventId = (int)ErrorCode.Runtime_Error_100272,
            Level = LogLevel.Information,
            Message = "Activating silo instance: {Data}"
        )]
        private partial void LogActivateSiloInstance(SiloInstanceTableEntry data);

        [LoggerMessage(
            EventId = (int)ErrorCode.Runtime_Error_100277,
            Level = LogLevel.Debug,
            Message = "Searching for active gateway silos for deployment {DeploymentId}."
        )]
        private partial void LogDebugSearchingGateway(string deploymentId);

        [LoggerMessage(
            EventId = (int)ErrorCode.Runtime_Error_100278,
            Level = LogLevel.Information,
            Message = "Found {GatewaySiloCount} active Gateway Silos for deployment {DeploymentId}."
        )]
        private partial void LogFoundGateway(int gatewaySiloCount, string deploymentId);

        [LoggerMessage(
            EventId = (int)ErrorCode.Runtime_Error_100331,
            Level = LogLevel.Error,
            Message = "Error searching for active gateway silos for deployment {DeploymentId} "
        )]
        private partial void LogErrorSearchingGateway(Exception exception, string deploymentId);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "InsertSiloEntryConditionally failed with httpStatusCode={HttpStatusCode}, restStatus={RestStatus}"
        )]
        private partial void LogTraceInsertSiloEntryConditionallyFailed(HttpStatusCode httpStatusCode, string? restStatus);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "UpdateSiloEntryConditionally failed with httpStatusCode={HttpStatusCode}, restStatus={RestStatus}"
        )]
        private partial void LogTraceUpdateSiloEntryConditionallyFailed(HttpStatusCode httpStatusCode, string? restStatus);
    }

    internal readonly record struct MembershipTableQueryResult(
        List<(SiloInstanceTableEntry Entity, string ETag)> Entries,
        bool IsPaginated);

    internal interface IMembershipTableReadStorage
    {
        Task<MembershipTableQueryResult> ReadAllTableEntriesForPartitionAsync(
            string partitionKey,
            CancellationToken cancellationToken = default);
    }

    internal sealed class AzureMembershipTableReadStorage(AzureTableDataManager<SiloInstanceTableEntry> storage)
        : IMembershipTableReadStorage
    {
        public async Task<MembershipTableQueryResult> ReadAllTableEntriesForPartitionAsync(
            string partitionKey,
            CancellationToken cancellationToken = default)
        {
            var result = await storage.ReadAllTableEntriesForPartitionWithPaginationAsync(partitionKey, cancellationToken);
            return new(result.Entries, result.IsPaginated);
        }
    }
}
