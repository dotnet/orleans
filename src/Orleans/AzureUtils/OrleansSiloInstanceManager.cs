/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

﻿using System;
using System.Collections.Generic;
using System.Data.Services.Common;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans.Runtime;


namespace Orleans.AzureUtils
{
    internal class SiloInstanceTableEntry : TableEntity
    {
        public string DeploymentId { get; set; }    // PartitionKey
        public string Address { get; set; }         // RowKey
        public string Port { get; set; }            // RowKey
        public string Generation { get; set; }      // RowKey

        public string HostName { get; set; }        // Mandatory
        public string Status { get; set; }          // Mandatory
        public string ProxyPort { get; set; }       // Optional
        public string Primary { get; set; }         // Optional - should be depricated

        public string RoleName { get; set; }        // Optional - only for Azure role
        public string InstanceName { get; set; }    // Optional - only for Azure role
        public string UpdateZone { get; set; }         // Optional - only for Azure role
        public string FaultZone { get; set; }          // Optional - only for Azure role

        public string SuspectingSilos { get; set; }          // For liveness
        public string SuspectingTimes { get; set; }          // For liveness

        public string StartTime       { get; set; }          // Time this silo was started. For diagnostics.
        public string IAmAliveTime    { get; set; }           // Time this silo updated it was alive. For diagnostics.
        public string MembershipVersion      { get; set; }               // Special version row (for serializing table updates). // We'll have a designated row with only MembershipVersion column.

        internal const string TABLE_VERSION_ROW = "VersionRow"; // Row key for version row.
        internal const char Seperator = '-';

        public static string ConstructRowKey(SiloAddress silo)
        {
            return String.Format("{0}-{1}-{2}", silo.Endpoint.Address, silo.Endpoint.Port, silo.Generation);
        }
        internal static SiloAddress UnpackRowKey(string rowKey)
        {
            var debugInfo = "UnpackRowKey";
            try
            {
#if DEBUG
                debugInfo = String.Format("UnpackRowKey: RowKey={0}", rowKey);
                Trace.TraceInformation(debugInfo);
#endif
                int idx1 = rowKey.IndexOf(Seperator);
                int idx2 = rowKey.LastIndexOf(Seperator);
#if DEBUG
                debugInfo = String.Format("UnpackRowKey: RowKey={0} Idx1={1} Idx2={2}", rowKey, idx1, idx2);
#endif
                var addressStr = rowKey.Substring(0, idx1);
                var portStr = rowKey.Substring(idx1 + 1, idx2 - idx1 - 1);
                var genStr = rowKey.Substring(idx2 + 1);
#if DEBUG
                debugInfo = String.Format("UnpackRowKey: RowKey={0} -> Address={1} Port={2} Generation={3}", rowKey, addressStr, portStr, genStr);
                Trace.TraceInformation(debugInfo);
#endif
                IPAddress address = IPAddress.Parse(addressStr);
                int port = Int32.Parse(portStr);
                int generation = Int32.Parse(genStr);
                return SiloAddress.New(new IPEndPoint(address, port), generation);
            }
            catch (Exception exc)
            {
                throw new AggregateException("Error from " + debugInfo, exc);
            }
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            if (RowKey.Equals(TABLE_VERSION_ROW))
            {
                sb.Append("VersionRow [").Append(DeploymentId);
                sb.Append(" Deployment=").Append(DeploymentId);
                sb.Append(" MembershipVersion=").Append(MembershipVersion);
                sb.Append("]");
            }
            else
            {
                sb.Append("OrleansSilo [");
                sb.Append(" Deployment=").Append(DeploymentId);
                sb.Append(" LocalEndpoint=").Append(Address);
                sb.Append(" LocalPort=").Append(Port);
                sb.Append(" Generation=").Append(Generation);

                sb.Append(" Host=").Append(HostName);
                sb.Append(" Status=").Append(Status);
                sb.Append(" ProxyPort=").Append(ProxyPort);
                sb.Append(" Primary=").Append(Primary);

                if (!string.IsNullOrEmpty(RoleName)) sb.Append(" RoleName=").Append(RoleName);
                sb.Append(" Instance=").Append(InstanceName);
                sb.Append(" UpgradeZone=").Append(UpdateZone);
                sb.Append(" FaultZone=").Append(FaultZone);

                if (!string.IsNullOrEmpty(SuspectingSilos)) sb.Append(" SuspectingSilos=").Append(SuspectingSilos);
                if (!string.IsNullOrEmpty(SuspectingTimes)) sb.Append(" SuspectingTimes=").Append(SuspectingTimes);
                sb.Append(" StartTime=").Append(StartTime);
                sb.Append(" IAmAliveTime=").Append(IAmAliveTime);
                sb.Append("]");
            }
            return sb.ToString();
        }
    }

    internal class OrleansSiloInstanceManager
    {
        public string TableName { get { return INSTANCE_TABLE_NAME; } }

        private const string INSTANCE_TABLE_NAME = "OrleansSiloInstances";

        private readonly string INSTANCE_STATUS_CREATED = SiloStatus.Created.ToString();  //"Created";
        private readonly string INSTANCE_STATUS_ACTIVE = SiloStatus.Active.ToString();    //"Active";
        private readonly string INSTANCE_STATUS_DEAD = SiloStatus.Dead.ToString();        //"Dead";

        private readonly AzureTableDataManager<SiloInstanceTableEntry> storage;
        private readonly TraceLogger logger;

        private static readonly TimeSpan initTimeout = AzureTableDefaultPolicies.TableCreationTimeout;

        public string DeploymentId { get; private set; }

        private OrleansSiloInstanceManager(string deploymentId, string storageConnectionString)
        {
            DeploymentId = deploymentId;
            logger = TraceLogger.GetLogger(this.GetType().Name, TraceLogger.LoggerType.Runtime);
            storage = new AzureTableDataManager<SiloInstanceTableEntry>(
                INSTANCE_TABLE_NAME, storageConnectionString, logger);
        }

        public static async Task<OrleansSiloInstanceManager> GetManager(string deploymentId, string storageConnectionString)
        {
            var instance = new OrleansSiloInstanceManager(deploymentId, storageConnectionString);
            try
            {
                await instance.storage.InitTableAsync()
                    .WithTimeout(initTimeout);

            }
            catch (TimeoutException)
            {
                instance.logger.Fail(ErrorCode.AzureTable_32, String.Format("Unable to create or connect to the Azure table in {0}", initTimeout));
            }
            catch (Exception ex)
            {
                instance.logger.Fail(ErrorCode.AzureTable_33, String.Format("Exception trying to create or connect to the Azure table: {0}", ex));
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

        public IPEndPoint FindPrimarySiloEndpoint()
        {
            SiloInstanceTableEntry primarySilo = FindPrimarySilo();
            if (primarySilo == null) return null;

            int port = 0;
            if (!string.IsNullOrEmpty(primarySilo.Port))
            {
                int.TryParse(primarySilo.Port, out port);
            }
            return new IPEndPoint(IPAddress.Parse(primarySilo.Address), port);
        }

        public List<Uri> FindAllGatewayProxyEndpoints()
        {
            IEnumerable<SiloInstanceTableEntry> gatewaySiloInstances = FindAllGatewaySilos();
            return gatewaySiloInstances.Select(gateway => gateway.ToGatewayUri()).ToList();
        }

        private SiloInstanceTableEntry FindPrimarySilo()
        {
            logger.Info(ErrorCode.Runtime_Error_100275, "Searching for active primary silo for deployment {0} ...", this.DeploymentId);
            string primary = true.ToString();

            Expression<Func<SiloInstanceTableEntry, bool>> query = instance =>
                instance.PartitionKey == this.DeploymentId
                && instance.Status == INSTANCE_STATUS_ACTIVE
                && instance.Primary == primary;

            var queryResults = storage.ReadTableEntriesAndEtagsAsync(query)
                                 .WaitForResultWithThrow(AzureTableDefaultPolicies.TableOperationTimeout);

            var primarySilo = default(SiloInstanceTableEntry);
            List<SiloInstanceTableEntry> primarySilosList = queryResults.Select(entity => entity.Item1).ToList();

            if (primarySilosList.Count == 0)
            {
                logger.Error(ErrorCode.Runtime_Error_100310, "Could not find Primary Silo");
            }
            else
            {
                primarySilo = primarySilosList.FirstOrDefault();
                logger.Info(ErrorCode.Runtime_Error_100276, "Found Primary Silo: {0}", primarySilo);
            }
            return primarySilo;
        }

        private IEnumerable<SiloInstanceTableEntry> FindAllGatewaySilos()
        {
            logger.Info(ErrorCode.Runtime_Error_100277, "Searching for active gateway silos for deployment {0} ...", this.DeploymentId);
            const string zeroPort = "0";

            Expression<Func<SiloInstanceTableEntry, bool>> query = instance =>
                instance.PartitionKey == this.DeploymentId
                && instance.Status == INSTANCE_STATUS_ACTIVE
                && instance.ProxyPort != zeroPort;

            var queryResults = storage.ReadTableEntriesAndEtagsAsync(query)
                                .WaitForResultWithThrow(AzureTableDefaultPolicies.TableOperationTimeout);

            List<SiloInstanceTableEntry> gatewaySiloInstances = queryResults.Select(entity => entity.Item1).ToList();

            logger.Info(ErrorCode.Runtime_Error_100278, "Found {0} active Gateway Silos.", gatewaySiloInstances.Count);
            return gatewaySiloInstances;
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
                    if (e1.InstanceName == null) return (e2.InstanceName == null) ? 0 : -1;
                    if (e2.InstanceName == null) return (e1.InstanceName == null) ? 0 : 1;
                    return String.CompareOrdinal(e1.InstanceName, e2.InstanceName);
                });
            foreach (SiloInstanceTableEntry entry in entries)
            {
                sb.AppendLine(String.Format("[IP {0}:{1}:{2}, {3}, Instance={4}, Status={5}]", entry.Address, entry.Port, entry.Generation,
                    entry.HostName, entry.InstanceName, entry.Status));
            }
            return sb.ToString();
        }

        #region Silo instance table storage operations

        internal Task<string> MergeTableEntryAsync(SiloInstanceTableEntry data)
        {
            return storage.MergeTableEntryAsync(data, AzureStorageUtils.ANY_ETAG);
        }

        internal Task<Tuple<SiloInstanceTableEntry, string>> ReadSingleTableEntryAsync(string partitionKey, string rowKey)
        {
            return storage.ReadSingleTableEntryAsync(partitionKey, rowKey);
        }

        internal async Task<int> DeleteTableEntries(string deploymentId)
        {
            if (deploymentId == null) throw new ArgumentNullException("deploymentId");

            var entries = await storage.ReadAllTableEntriesForPartitionAsync(deploymentId);
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

            Expression<Func<SiloInstanceTableEntry, bool>> query = instance =>
                instance.PartitionKey == DeploymentId
                && (instance.RowKey == rowKey || instance.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW);

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
        /// <param name="siloEntry">Silo Entry to be written</param>
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

                if (logger.IsVerbose2) logger.Verbose2("InsertSiloEntryConditionally failed with httpStatusCode={0}, restStatus={1}", httpStatusCode, restStatus);
                if (AzureStorageUtils.IsContentionError(httpStatusCode)) return false;

                throw;
            }
        }

        /// <summary>
        /// Insert (create new) row entry
        /// </summary>
        /// <param name="siloEntry">Silo Entry to be written</param>
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

                if (logger.IsVerbose2) logger.Verbose2("InsertSiloEntryConditionally failed with httpStatusCode={0}, restStatus={1}", httpStatusCode, restStatus);
                if (AzureStorageUtils.IsContentionError(httpStatusCode)) return false;
                
                throw;
            }
        }

        /// <summary>
        /// Conditionally update the row for this entry, but only if the eTag matches with the current record in data store
        /// </summary>
        /// <param name="siloEntry">Silo Entry to be written</param>
        /// <param name="eTag">ETag value for the entry being updated</param>
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

                if (logger.IsVerbose2) logger.Verbose2("UpdateSiloEntryConditionally failed with httpStatusCode={0}, restStatus={1}", httpStatusCode, restStatus);
                if (AzureStorageUtils.IsContentionError(httpStatusCode)) return false;
                
                throw;
            }
        }

        #endregion
    }
}