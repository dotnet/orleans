using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans.Runtime;
using Orleans.Clustering.AzureStorage;
using Orleans.Clustering.AzureStorage.Utilities;

namespace Orleans.AzureUtils
{
    internal class OrleansSiloInstanceManager
    {
        public string TableName { get { return INSTANCE_TABLE_NAME; } }

        private const string INSTANCE_TABLE_NAME = "OrleansSiloInstances";

        private readonly string INSTANCE_STATUS_CREATED = SiloStatus.Created.ToString();  //"Created";
        private readonly string INSTANCE_STATUS_ACTIVE = SiloStatus.Active.ToString();    //"Active";
        private readonly string INSTANCE_STATUS_DEAD = SiloStatus.Dead.ToString();        //"Dead";

        private readonly AzureTableDataManager<SiloInstanceTableEntry> storage;
        private readonly ILogger logger;

        internal static TimeSpan initTimeout = AzureTableDefaultPolicies.TableCreationTimeout;

        public string DeploymentId { get; private set; }

        private OrleansSiloInstanceManager(string clusterId, string storageConnectionString, ILoggerFactory loggerFactory)
        {
            DeploymentId = clusterId;
            logger = loggerFactory.CreateLogger<OrleansSiloInstanceManager>();
            storage = new AzureTableDataManager<SiloInstanceTableEntry>(
                INSTANCE_TABLE_NAME, storageConnectionString, loggerFactory);
        }

        public static async Task<OrleansSiloInstanceManager> GetManager(string clusterId, string storageConnectionString, ILoggerFactory loggerFactory)
        {
            var instance = new OrleansSiloInstanceManager(clusterId, storageConnectionString, loggerFactory);
            try
            {
                await instance.storage.InitTableAsync()
                    .WithTimeout(initTimeout);
            }
            catch (TimeoutException te)
            {
                string errorMsg = String.Format("Unable to create or connect to the Azure table in {0}", initTimeout);
                instance.logger.Error((int)TableStorageErrorCode.AzureTable_32, errorMsg, te);
                throw new OrleansException(errorMsg, te);
            }
            catch (Exception ex)
            {
                string errorMsg = String.Format("Exception trying to create or connect to the Azure table: {0}", ex.Message);
                instance.logger.Error((int)TableStorageErrorCode.AzureTable_33, errorMsg, ex);
                throw new OrleansException(errorMsg, ex);
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
            logger.Info(ErrorCode.Runtime_Error_100270, "Registering silo instance: {0}", entry.ToString());
            storage.UpsertTableEntryAsync(entry)
                .WaitWithThrow(AzureTableDefaultPolicies.TableOperationTimeout);
        }

        public void UnregisterSiloInstance(SiloInstanceTableEntry entry)
        {
            entry.Status = INSTANCE_STATUS_DEAD;
            logger.Info(ErrorCode.Runtime_Error_100271, "Unregistering silo instance: {0}", entry.ToString());
            storage.UpsertTableEntryAsync(entry)
                .WaitWithThrow(AzureTableDefaultPolicies.TableOperationTimeout);
        }

        public void ActivateSiloInstance(SiloInstanceTableEntry entry)
        {
            logger.Info(ErrorCode.Runtime_Error_100272, "Activating silo instance: {0}", entry.ToString());
            entry.Status = INSTANCE_STATUS_ACTIVE;
            storage.UpsertTableEntryAsync(entry)
                .WaitWithThrow(AzureTableDefaultPolicies.TableOperationTimeout);
        }

        public async Task<IList<Uri>> FindAllGatewayProxyEndpoints()
        {
            IEnumerable<SiloInstanceTableEntry> gatewaySiloInstances = await FindAllGatewaySilos();
            return gatewaySiloInstances.Select(ConvertToGatewayUri).ToList();
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

            SiloAddress address = SiloAddress.New(new IPEndPoint(IPAddress.Parse(gateway.Address), proxyPort), gen);
            return address.ToGatewayUri();
        }

        private async Task<IEnumerable<SiloInstanceTableEntry>> FindAllGatewaySilos()
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug(ErrorCode.Runtime_Error_100277, "Searching for active gateway silos for deployment {0}.", this.DeploymentId);
            const string zeroPort = "0";

            try
            {
                string filterOnPartitionKey = TableQuery.GenerateFilterCondition(nameof(SiloInstanceTableEntry.PartitionKey), QueryComparisons.Equal,
                    this.DeploymentId);
                string filterOnStatus = TableQuery.GenerateFilterCondition(nameof(SiloInstanceTableEntry.Status), QueryComparisons.Equal,
                    INSTANCE_STATUS_ACTIVE);
                string filterOnProxyPort = TableQuery.GenerateFilterCondition(nameof(SiloInstanceTableEntry.ProxyPort), QueryComparisons.NotEqual, zeroPort);
                string query = TableQuery.CombineFilters(filterOnPartitionKey, TableOperators.And, TableQuery.CombineFilters(filterOnStatus, TableOperators.And, filterOnProxyPort));
                var queryResults = await storage.ReadTableEntriesAndEtagsAsync(query)
                                    .WithTimeout(AzureTableDefaultPolicies.TableOperationTimeout);

                List<SiloInstanceTableEntry> gatewaySiloInstances = queryResults.Select(entity => entity.Item1).ToList();

                logger.Info(ErrorCode.Runtime_Error_100278, "Found {0} active Gateway Silos for deployment {1}.", gatewaySiloInstances.Count, this.DeploymentId);
                return gatewaySiloInstances;
            }catch(Exception exc)
            {
                logger.Error(ErrorCode.Runtime_Error_100331, string.Format("Error searching for active gateway silos for deployment {0} ", this.DeploymentId), exc);
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
            return storage.MergeTableEntryAsync(data, AzureStorageUtils.ANY_ETAG); // we merge this without checking eTags.
        }

        internal Task<Tuple<SiloInstanceTableEntry, string>> ReadSingleTableEntryAsync(string partitionKey, string rowKey)
        {
            return storage.ReadSingleTableEntryAsync(partitionKey, rowKey);
        }

        internal async Task<int> DeleteTableEntries(string clusterId)
        {
            if (clusterId == null) throw new ArgumentNullException(nameof(clusterId));

            var entries = await storage.ReadAllTableEntriesForPartitionAsync(clusterId);
            var entriesList = new List<Tuple<SiloInstanceTableEntry, string>>(entries);
            if (entriesList.Count <= AzureTableDefaultPolicies.MAX_BULK_UPDATE_ROWS)
            {
                await storage.DeleteTableEntriesAsync(entriesList);
            }else
            {
                List<Task> tasks = new List<Task>();
                foreach (var batch in entriesList.BatchIEnumerable(AzureTableDefaultPolicies.MAX_BULK_UPDATE_ROWS))
                {
                    tasks.Add(storage.DeleteTableEntriesAsync(batch));
                }
                await Task.WhenAll(tasks);
            }
            return entriesList.Count();
        }

        internal async Task<List<Tuple<SiloInstanceTableEntry, string>>> FindSiloEntryAndTableVersionRow(SiloAddress siloAddress)
        {
            string rowKey = SiloInstanceTableEntry.ConstructRowKey(siloAddress);

            string filterOnPartitionKey = TableQuery.GenerateFilterCondition(nameof(SiloInstanceTableEntry.PartitionKey), QueryComparisons.Equal,
                    this.DeploymentId);
            string filterOnRowKey1 = TableQuery.GenerateFilterCondition(nameof(SiloInstanceTableEntry.RowKey), QueryComparisons.Equal,
                rowKey);
            string filterOnRowKey2 = TableQuery.GenerateFilterCondition(nameof(SiloInstanceTableEntry.RowKey), QueryComparisons.Equal, SiloInstanceTableEntry.TABLE_VERSION_ROW);
            string query = TableQuery.CombineFilters(filterOnPartitionKey, TableOperators.And, TableQuery.CombineFilters(filterOnRowKey1, TableOperators.Or, filterOnRowKey2));

            var queryResults = await storage.ReadTableEntriesAndEtagsAsync(query);

            var asList = queryResults.ToList();
            if (asList.Count < 1 || asList.Count > 2)
                throw new KeyNotFoundException(string.Format("Could not find table version row or found too many entries. Was looking for key {0}, found = {1}", siloAddress.ToLongString(), Utils.EnumerableToString(asList)));

            int numTableVersionRows = asList.Count(tuple => tuple.Item1.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW);
            if (numTableVersionRows < 1)
                throw new KeyNotFoundException(string.Format("Did not read table version row. Read = {0}", Utils.EnumerableToString(asList)));

            if (numTableVersionRows > 1)
                throw new KeyNotFoundException(string.Format("Read {0} table version rows, while was expecting only 1. Read = {1}", numTableVersionRows, Utils.EnumerableToString(asList)));

            return asList;
        }

        internal async Task<List<Tuple<SiloInstanceTableEntry, string>>> FindAllSiloEntries()
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
                if (versionRow != null && versionRow.Item1 != null)
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
                if (!AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus)) throw;

                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("InsertSiloEntryConditionally failed with httpStatusCode={0}, restStatus={1}", httpStatusCode, restStatus);
                if (AzureStorageUtils.IsContentionError(httpStatusCode)) return false;

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
                if (!AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus)) throw;

                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("InsertSiloEntryConditionally failed with httpStatusCode={0}, restStatus={1}", httpStatusCode, restStatus);
                if (AzureStorageUtils.IsContentionError(httpStatusCode)) return false;

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
                if (!AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus)) throw;

                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("UpdateSiloEntryConditionally failed with httpStatusCode={0}, restStatus={1}", httpStatusCode, restStatus);
                if (AzureStorageUtils.IsContentionError(httpStatusCode)) return false;

                throw;
            }
        }
    }
}
