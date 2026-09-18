using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Azure.Data.Tables.Models;
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
            AzureStorageOperationOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instance = new OrleansSiloInstanceManager(clusterId, loggerFactory, options);
            try
            {
                await instance.storage.InitTableAsync(cancellationToken);
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

            return (min, max);
        }

        public Task<string> RegisterSiloInstance(SiloInstanceTableEntry entry)
        {
            entry.Status = INSTANCE_STATUS_CREATED;
            LogRegisterSiloInstance(entry);
            return storage.UpsertTableEntryAsync(entry);
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
                int.TryParse(gateway.ProxyPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out proxyPort);

            int gen = 0;
            if (!string.IsNullOrEmpty(gateway.Generation))
                int.TryParse(gateway.Generation, NumberStyles.Integer, CultureInfo.InvariantCulture, out gen);

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
            }
            catch (Exception exc)
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
            sb.Append(string.Format(CultureInfo.CurrentCulture, "Deployment {0}. Silos: ", DeploymentId));

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
                sb.AppendLine(string.Format(
                    CultureInfo.CurrentCulture,
                    "[IP {0}:{1}:{2}, {3}, Instance={4}, Status={5}]",
                    entry.Address,
                    entry.Port,
                    entry.Generation,
                    entry.HostName,
                    entry.SiloName,
                    entry.Status));
            }
            return sb.ToString();
        }

        internal Task<string> MergeTableEntryAsync(SiloInstanceTableEntry data, CancellationToken cancellationToken = default)
        {
            return storage.MergeTableEntryAsync(data, AzureTableUtils.ANY_ETAG, cancellationToken);
        }

        internal Task<(SiloInstanceTableEntry? Entity, string? ETag)> ReadSingleTableEntryAsync(string partitionKey, string rowKey)
        {
            return storage.ReadSingleTableEntryAsync(partitionKey, rowKey);
        }

        internal async Task<int> DeleteTableEntries(string clusterId, CancellationToken cancellationToken = default)
        {
            if (clusterId == null) throw new ArgumentNullException(nameof(clusterId));

            var entries = await storage.ReadAllTableEntriesForPartitionAsync(clusterId, cancellationToken);

            await DeleteEntriesBatch(entries, cancellationToken);

            return entries.Count;
        }

        /// <summary>
        /// Deletes Dead entries whose latest start, heartbeat, and suspicion times precede the cutoff,
        /// using row etags to protect concurrent updates and preserving the membership version.
        /// The caller schedules subsequent attempts after native contention.
        /// </summary>
        public async Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate, CancellationToken cancellationToken = default)
        {
            var entries = await FindAllSiloEntries(cancellationToken);
            var defunct = entries
                .Where(entry => !SiloInstanceTableEntry.IsVersionRow(entry.Entity.RowKey)
                    && entry.Entity.Status == INSTANCE_STATUS_DEAD
                    && GetEffectiveUpdateTime(entry.Entity) < beforeDate.UtcDateTime)
                .ToList();

            await DeleteEntriesBatch(defunct, cancellationToken);
        }

        private static DateTime GetEffectiveUpdateTime(SiloInstanceTableEntry entry)
        {
            var result = string.IsNullOrEmpty(entry.StartTime) ? default : LogFormatter.ParseDate(entry.StartTime);
            if (!string.IsNullOrEmpty(entry.IAmAliveTime))
            {
                var heartbeat = LogFormatter.ParseDate(entry.IAmAliveTime);
                result = heartbeat > result ? heartbeat : result;
            }

            if (!string.IsNullOrEmpty(entry.SuspectingTimes))
            {
                foreach (var value in entry.SuspectingTimes.Split('|'))
                {
                    var suspectTime = LogFormatter.ParseDate(value);
                    result = suspectTime > result ? suspectTime : result;
                }
            }

            return result;
        }

        private async Task DeleteEntriesBatch(List<(SiloInstanceTableEntry, string)> entriesList, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entriesList.Count <= this.storagePolicyOptions.MaxBulkUpdateRows)
            {
                await storage.DeleteTableEntriesAsync(entriesList, cancellationToken);
            }
            else
            {
                var deletions = Task.WhenAll(entriesList.BatchIEnumerable(this.storagePolicyOptions.MaxBulkUpdateRows)
                    .Select(batch => storage.DeleteTableEntriesAsync(batch, cancellationToken)));
                try
                {
                    await deletions;
                }
                catch when (deletions.Exception is { InnerExceptions.Count: > 1 })
                {
                    // Preserve every failed batch for the caller.
                    throw deletions.Exception;
                }
            }
        }

        internal async Task<List<(SiloInstanceTableEntry, string)>> FindSiloEntryAndTableVersionRow(SiloAddress siloAddress, CancellationToken cancellationToken = default)
        {
            string rowKey = SiloInstanceTableEntry.ConstructRowKey(siloAddress);

            var filter = TableClient.CreateQueryFilter($"(PartitionKey eq {DeploymentId}) and ((RowKey eq {rowKey}) or (RowKey eq {SiloInstanceTableEntry.TABLE_VERSION_ROW}))");
            var queryResults = await storage.ReadTableEntriesAndEtagsAsync(filter, cancellationToken);
            if (queryResults.Count < 1 || queryResults.Count > 2)
                throw new KeyNotFoundException(string.Format(
                    CultureInfo.CurrentCulture,
                    "Could not find table version row or found too many entries. Was looking for key {0}, found = {1}",
                    siloAddress,
                    Utils.EnumerableToString(queryResults)));

            var numTableVersionRows = 0;
            foreach (var entry in queryResults)
            {
                if (entry.Item1.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW)
                {
                    numTableVersionRows++;
                }
            }

            if (numTableVersionRows < 1)
                throw new KeyNotFoundException(string.Format(
                    CultureInfo.CurrentCulture,
                    "Did not read table version row. Read = {0}",
                    Utils.EnumerableToString(queryResults)));

            if (numTableVersionRows > 1)
                throw new KeyNotFoundException(string.Format(
                    CultureInfo.CurrentCulture,
                    "Read {0} table version rows, while was expecting only 1. Read = {1}",
                    numTableVersionRows,
                    Utils.EnumerableToString(queryResults)));

            return queryResults;
        }

        internal async Task<List<(SiloInstanceTableEntry Entity, string ETag)>> FindAllSiloEntries(
            CancellationToken cancellationToken = default)
        {
            for (var attempt = 0; attempt < MaxMembershipSnapshotAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var query = await membershipTableReadStorage.ReadAllTableEntriesForPartitionAsync(DeploymentId, cancellationToken);
                if (CanAcceptSnapshot(query.Entries))
                {
                    return RemoveBoundaryVersionRows(query.Entries);
                }
            }

            throw new InconsistentStateException(
                $"Unable to read a consistent membership snapshot for cluster '{DeploymentId}' from table '{TableName}' after {MaxMembershipSnapshotAttempts} attempts.");
        }

        private static bool CanAcceptSnapshot(List<(SiloInstanceTableEntry Entity, string ETag)> entries)
        {
            var version = ValidateAllSiloEntries(entries);
            if (entries[0].Entity.RowKey != SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN
                || entries[^1].Entity.RowKey != SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX)
            {
                throw new KeyNotFoundException("The membership query must include both ordered boundary version rows.");
            }

            var before = entries[0].Entity.MembershipVersion
                ?? throw new InvalidOperationException("The opening boundary row does not contain a membership version.");
            var after = entries[^1].Entity.MembershipVersion
                ?? throw new InvalidOperationException("The closing boundary row does not contain a membership version.");

            // Every canonical write changes both ordered markers atomically with the membership data.
            return string.Equals(before, after, StringComparison.Ordinal)
                && string.Equals(before, version, StringComparison.Ordinal);
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
                throw new KeyNotFoundException(string.Format(
                    CultureInfo.CurrentCulture,
                    "Could not find enough rows in the FindAllSiloEntries call. Found = {0}",
                    Utils.EnumerableToString(queryResults)));

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
                throw new KeyNotFoundException(string.Format(
                    CultureInfo.CurrentCulture,
                    "Did not find table version row. Read = {0}",
                    Utils.EnumerableToString(queryResults)));
            if (numTableVersionRows > 1)
                throw new KeyNotFoundException(string.Format(
                    CultureInfo.CurrentCulture,
                    "Read {0} table version rows, while was expecting only 1. Read = {1}",
                    numTableVersionRows,
                    Utils.EnumerableToString(queryResults)));

            return tableVersion;
        }

        /// <summary>
        /// Insert (create new) row entry
        /// </summary>
        internal async Task<bool> TryCreateTableVersionEntryAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var versionRow = await storage.ReadSingleTableEntryAsync(DeploymentId, SiloInstanceTableEntry.TABLE_VERSION_ROW, cancellationToken);
                if (versionRow.Entity != null)
                {
                    return false;
                }

                var entry = CreateTableVersionEntry(0);
                var boundaryEntries = CreateBoundaryVersionEntries(entry);
                await storage.CreateTableEntriesAsync([entry, boundaryEntries.Min, boundaryEntries.Max], cancellationToken);
                return true;
            }
            catch (Exception exc)
            {
                if (!AzureTableUtils.EvaluateException(exc, out var httpStatusCode, out var restStatus)) throw;

                LogTraceInsertSiloEntryConditionallyFailed(httpStatusCode, restStatus);
                if (httpStatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed) return false;

                throw;
            }
        }

        /// <summary>
        /// Insert (create new) row entry
        /// </summary>
        /// <param name="siloEntry">Silo Entry to be written</param>
        /// <param name="tableVersionEntry">Version row to update</param>
        /// <param name="tableVersionEtag">Version row eTag</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        internal async Task<bool> InsertSiloEntryConditionally(SiloInstanceTableEntry siloEntry, SiloInstanceTableEntry tableVersionEntry, string tableVersionEtag, CancellationToken cancellationToken = default)
        {
            try
            {
                var boundaryEntries = CreateBoundaryVersionEntries(tableVersionEntry);
                await storage.CreateAndUpdateTableEntriesAsync(
                    siloEntry,
                    (tableVersionEntry, tableVersionEtag),
                    (boundaryEntries.Min, boundaryEntries.Max),
                    cancellationToken);
                return true;
            }
            catch (Exception exc)
            {
                if (!AzureTableUtils.EvaluateException(exc, out var httpStatusCode, out var restStatus)) throw;

                LogTraceInsertSiloEntryConditionallyFailed(httpStatusCode, restStatus);
                if (httpStatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed
                    || exc is RequestFailedException requestFailed && IsRowNotFound(requestFailed)) return false;

                throw;
            }
        }

        /// <summary>
        /// Atomically replaces a membership row under the canonical table-version etag.
        /// </summary>
        /// <param name="siloEntry">Silo Entry to be written</param>
        /// <param name="tableVersionEntry">Version row to update</param>
        /// <param name="versionEtag">ETag value for the version row</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        internal async Task<bool> UpdateSiloEntryConditionally(SiloInstanceTableEntry siloEntry, SiloInstanceTableEntry tableVersionEntry, string versionEtag, CancellationToken cancellationToken = default)
        {
            try
            {
                var boundaryEntries = CreateBoundaryVersionEntries(tableVersionEntry);
                await storage.UpdateTableEntriesAsync(
                    (siloEntry, AzureTableUtils.ANY_ETAG),
                    (tableVersionEntry, versionEtag),
                    (boundaryEntries.Min, boundaryEntries.Max),
                    cancellationToken);
                return true;
            }
            catch (Exception exc)
            {
                if (!AzureTableUtils.EvaluateException(exc, out var httpStatusCode, out var restStatus)) throw;

                LogTraceUpdateSiloEntryConditionallyFailed(httpStatusCode, restStatus);
                if (httpStatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed
                    || exc is RequestFailedException requestFailed && IsRowNotFound(requestFailed)) return false;

                throw;
            }
        }

        private static bool IsRowNotFound(RequestFailedException exception)
            => exception.Status == (int)HttpStatusCode.NotFound
                && (exception.ErrorCode == TableErrorCode.ResourceNotFound.ToString()
                    || exception.ErrorCode == TableErrorCode.EntityNotFound.ToString());

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
