using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Orleans.Clustering.AzureStorage;
using Orleans.Clustering.AzureStorage.Utilities;
using Orleans.Runtime;

namespace Orleans.AzureUtils
{
    internal class OrleansSiloInstanceManager
    {
        public string TableName { get; }

        private const string INSTANCE_STATUS_CREATED = nameof(SiloStatus.Created);  //"Created";
        private const string INSTANCE_STATUS_ACTIVE = nameof(SiloStatus.Active);    //"Active";
        private const string INSTANCE_STATUS_DEAD = nameof(SiloStatus.Dead);        //"Dead";

        private readonly AzureTableDataManager<SiloInstanceTableEntry> storage;
        private readonly ILogger logger;
        private readonly AzureStoragePolicyOptions storagePolicyOptions;

        public string DeploymentId { get; private set; }

        private OrleansSiloInstanceManager(
            string clusterId,
            ILoggerFactory loggerFactory,
            AzureStorageOperationOptions options)
        {
            DeploymentId = clusterId;
            TableName = options.TableName;
            logger = loggerFactory.CreateLogger<OrleansSiloInstanceManager>();
            storage = new AzureTableDataManager<SiloInstanceTableEntry>(
                options,
                loggerFactory.CreateLogger<AzureTableDataManager<SiloInstanceTableEntry>>());
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
                instance.logger.LogError((int)TableStorageErrorCode.AzureTable_33, ex, "Exception trying to create or connect to the Azure table {TableName}", instance.storage.TableName);
                throw new OrleansException($"Exception trying to create or connect to the Azure table {instance.storage.TableName}", ex);
            }
            return instance;
        }

        public SiloInstanceTableEntry CreateTableVersionEntry(int tableVersion)
        {
            return new SiloInstanceTableEntry
            {
                DeploymentId = DeploymentId,
                PartitionKey = DeploymentId,
                RowKey = SiloInstanceTableEntry.TABLE_VERSION_ROW,
                MembershipVersion = tableVersion.ToString(CultureInfo.InvariantCulture)
            };
        }

        public void RegisterSiloInstance(SiloInstanceTableEntry entry)
        {
            entry.Status = INSTANCE_STATUS_CREATED;
            logger.LogInformation((int)ErrorCode.Runtime_Error_100270, "Registering silo instance: {Data}", entry.ToString());
            Task.WaitAll(new Task[] { storage.UpsertTableEntryAsync(entry) });
        }

        public Task<string> UnregisterSiloInstance(SiloInstanceTableEntry entry)
        {
            entry.Status = INSTANCE_STATUS_DEAD;
            logger.LogInformation((int)ErrorCode.Runtime_Error_100271, "Unregistering silo instance: {Data}", entry.ToString());
            return storage.UpsertTableEntryAsync(entry);
        }

        public Task<string> ActivateSiloInstance(SiloInstanceTableEntry entry)
        {
            logger.LogInformation((int)ErrorCode.Runtime_Error_100272, "Activating silo instance: {Data}", entry.ToString());
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

            SiloAddress address = SiloAddress.New(IPAddress.Parse(gateway.Address), proxyPort, gen);
            return address.ToGatewayUri();
        }

        public async Task<IList<Uri>> FindAllGatewayProxyEndpoints()
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug((int)ErrorCode.Runtime_Error_100277, "Searching for active gateway silos for deployment {DeploymentId}.", this.DeploymentId);

            try
            {
                const string Active = nameof(SiloStatus.Active);
                const string Zero = "0";
                var queryResults = await storage.ReadTableEntriesAndEtagsAsync(TableClient.CreateQueryFilter($"PartitionKey eq {DeploymentId} and Status eq {Active} and ProxyPort ne {Zero}"));

                var gatewaySiloInstances = queryResults.Select(entity => ConvertToGatewayUri(entity.Item1)).ToList();

                logger.LogInformation((int)ErrorCode.Runtime_Error_100278, "Found {GatewaySiloCount} active Gateway Silos for deployment {DeploymentId}.", gatewaySiloInstances.Count, this.DeploymentId);
                return gatewaySiloInstances;
            }catch(Exception exc)
            {
                logger.LogError((int)ErrorCode.Runtime_Error_100331, exc, "Error searching for active gateway silos for deployment {DeploymentId} ", this.DeploymentId);
                throw;
            }
        }

        public async Task<string> DumpSiloInstanceTable()
        {
            var queryResults = await storage.ReadAllTableEntriesForPartitionAsync(this.DeploymentId);

            SiloInstanceTableEntry[] entries = queryResults.Select(entry => entry.Item1).ToArray();

            var sb = new StringBuilder();
            sb.Append(String.Format("Deployment {0}. Silos: ", DeploymentId));

            // Loop through the results, displaying information about the entity
            Array.Sort(entries,
                (e1, e2) =>
                {
                    if (e1 == null) return (e2 == null) ? 0 : -1;
                    if (e2 == null) return (e1 == null) ? 0 : 1;
                    if (e1.SiloName == null) return (e2.SiloName == null) ? 0 : -1;
                    if (e2.SiloName == null) return (e1.SiloName == null) ? 0 : 1;
                    return String.CompareOrdinal(e1.SiloName, e2.SiloName);
                });
            foreach (SiloInstanceTableEntry entry in entries)
            {
                sb.AppendLine(String.Format("[IP {0}:{1}:{2}, {3}, Instance={4}, Status={5}]", entry.Address, entry.Port, entry.Generation,
                    entry.HostName, entry.SiloName, entry.Status));
            }
            return sb.ToString();
        }

        internal Task<string> MergeTableEntryAsync(SiloInstanceTableEntry data)
        {
            return storage.MergeTableEntryAsync(data, AzureTableUtils.ANY_ETAG); // we merge this without checking eTags.
        }

        internal Task<(SiloInstanceTableEntry, string)> ReadSingleTableEntryAsync(string partitionKey, string rowKey)
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
                .Where(entry => !string.Equals(SiloInstanceTableEntry.TABLE_VERSION_ROW, entry.Item1.RowKey)
                    && entry.Item1.Status != INSTANCE_STATUS_ACTIVE
                    && entry.Item1.Timestamp < beforeDate)
                .ToList();

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

            var asList = queryResults.ToList();
            if (asList.Count < 1 || asList.Count > 2)
                throw new KeyNotFoundException(string.Format("Could not find table version row or found too many entries. Was looking for key {0}, found = {1}", siloAddress, Utils.EnumerableToString(asList)));

            int numTableVersionRows = asList.Count(tuple => tuple.Item1.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW);
            if (numTableVersionRows < 1)
                throw new KeyNotFoundException(string.Format("Did not read table version row. Read = {0}", Utils.EnumerableToString(asList)));

            if (numTableVersionRows > 1)
                throw new KeyNotFoundException(string.Format("Read {0} table version rows, while was expecting only 1. Read = {1}", numTableVersionRows, Utils.EnumerableToString(asList)));

            return asList;
        }

        internal async Task<List<(SiloInstanceTableEntry, string)>> FindAllSiloEntries()
        {
            var queryResults = await storage.ReadAllTableEntriesForPartitionAsync(this.DeploymentId);

            var asList = queryResults.ToList();
            if (asList.Count < 1)
                throw new KeyNotFoundException(string.Format("Could not find enough rows in the FindAllSiloEntries call. Found = {0}", Utils.EnumerableToString(asList)));

            int numTableVersionRows = asList.Count(tuple => tuple.Item1.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW);
            if (numTableVersionRows < 1)
                throw new KeyNotFoundException(string.Format("Did not find table version row. Read = {0}", Utils.EnumerableToString(asList)));
            if (numTableVersionRows > 1)
                throw new KeyNotFoundException(string.Format("Read {0} table version rows, while was expecting only 1. Read = {1}", numTableVersionRows, Utils.EnumerableToString(asList)));

            return asList;
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

                SiloInstanceTableEntry entry = CreateTableVersionEntry(0);
                await storage.CreateTableEntryAsync(entry);
                return true;
            }
            catch (Exception exc)
            {
                HttpStatusCode httpStatusCode;
                string restStatus;
                if (!AzureTableUtils.EvaluateException(exc, out httpStatusCode, out restStatus)) throw;

                if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("InsertSiloEntryConditionally failed with httpStatusCode={StatusCode}, restStatus={RESTStatusCode}", httpStatusCode, restStatus);
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
                await storage.InsertTwoTableEntriesConditionallyAsync(siloEntry, tableVersionEntry, tableVersionEtag);
                return true;
            }
            catch (Exception exc)
            {
                HttpStatusCode httpStatusCode;
                string restStatus;
                if (!AzureTableUtils.EvaluateException(exc, out httpStatusCode, out restStatus)) throw;

                if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("InsertSiloEntryConditionally failed with httpStatusCode={StatusCode}, restStatus={RESTStatusCode}", httpStatusCode, restStatus);
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
                await storage.UpdateTwoTableEntriesConditionallyAsync(siloEntry, entryEtag, tableVersionEntry, versionEtag);
                return true;
            }
            catch (Exception exc)
            {
                HttpStatusCode httpStatusCode;
                string restStatus;
                if (!AzureTableUtils.EvaluateException(exc, out httpStatusCode, out restStatus)) throw;

                if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("UpdateSiloEntryConditionally failed with httpStatusCode={StatusCode}, restStatus={RESTStatusCode}", httpStatusCode, restStatus);
                if (AzureTableUtils.IsContentionError(httpStatusCode)) return false;

                throw;
            }
        }
    }
}
