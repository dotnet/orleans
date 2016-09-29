using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans.AzureUtils;
using Orleans.MultiCluster;

namespace Orleans.Runtime.MultiClusterNetwork
{
    /// <summary>
    /// Low level representation details and functionality for Azure-Table-Based Gossip Channels
    /// </summary>
    internal class GossipTableEntry : TableEntity
    {
        // used for partitioning table
        internal string GlobalServiceId { get { return PartitionKey; } }

        public DateTime GossipTimestamp { get; set; }   // timestamp of gossip entry

        #region gateway entry

        public string Status { get; set; }

        // all of the following are packed in rowkey

        public string ClusterId;

        public IPAddress Address;

        public int Port;

        public int Generation;

        public SiloAddress SiloAddress;

        #endregion

        #region configuration entry

        public string Clusters { get; set; }   // comma-separated list of clusters

        public string Comment { get; set; }

        #endregion

        internal const string CONFIGURATION_ROW = "CONFIG"; // Row key for configuration row.

        private const string Separator = ","; // safe because clusterid cannot contain commas
        private readonly static char[] SeparatorChars = Separator.ToCharArray();
        internal const string ClustersListSeparator = ","; // safe because clusterid cannot contain commas
        private readonly static char[] ClustersListSeparatorChars = ClustersListSeparator.ToCharArray();
        private const string RowKeyFormat = "{0}"+ Separator + "{1}" + Separator + "{2}" + Separator + "{3}";

        public static string ConstructRowKey(SiloAddress silo, string clusterid)
        {
            return String.Format(RowKeyFormat, clusterid, silo.Endpoint.Address, silo.Endpoint.Port, silo.Generation);
        }

        internal void ParseSiloAddressFromRowKey()
        {
            const string debugInfo = "ParseSiloAddressFromRowKey";
            try
            {
                var segments = RowKey.Split(SeparatorChars, 4);

                ClusterId = segments[0];
                Address = IPAddress.Parse(segments[1]);
                Port = Int32.Parse(segments[2]);
                Generation = Int32.Parse(segments[3]);

                this.SiloAddress = SiloAddress.New(new IPEndPoint(Address, Port), Generation);
            }
            catch (Exception exc)
            {
                throw new FormatException("Error from " + debugInfo, exc);
            }
        }

        internal MultiClusterConfiguration ToConfiguration()
        {
            var clusterlist = Clusters.Split(ClustersListSeparatorChars, StringSplitOptions.RemoveEmptyEntries);
            return new MultiClusterConfiguration(GossipTimestamp, clusterlist, Comment ?? "");
        }

        internal GatewayEntry ToGatewayEntry()
        {
            // call this only after already unpacking row key
            return new GatewayEntry()
            {
                ClusterId = ClusterId,
                SiloAddress = SiloAddress,
                Status = (GatewayStatus) Enum.Parse(typeof(GatewayStatus), Status),
                HeartbeatTimestamp = GossipTimestamp
            };
        }

        public override string ToString()
        {
            if (RowKey == CONFIGURATION_ROW)
                return ToConfiguration().ToString();
            else
                return string.Format("{0} {1}",
                    this.SiloAddress, this.Status);
        }
    }

    internal class GossipTableInstanceManager
    {
        public string TableName { get { return INSTANCE_TABLE_NAME; } }

        private const string INSTANCE_TABLE_NAME = "OrleansGossipTable";

        private readonly AzureTableDataManager<GossipTableEntry> storage;
        private readonly Logger logger;

        internal static TimeSpan initTimeout = AzureTableDefaultPolicies.TableCreationTimeout;

        public string GlobalServiceId { get; private set; }

        private GossipTableInstanceManager(Guid globalServiceId, string storageConnectionString, Logger logger)
        {
            GlobalServiceId = globalServiceId.ToString();
            this.logger = logger;
            storage = new AzureTableDataManager<GossipTableEntry>(
                INSTANCE_TABLE_NAME, storageConnectionString, logger);
        }

        public static async Task<GossipTableInstanceManager> GetManager(Guid globalServiceId, string storageConnectionString, Logger logger)
        {
            if (logger == null) throw new ArgumentNullException("logger");
            
            var instance = new GossipTableInstanceManager(globalServiceId, storageConnectionString, logger);
            try
            {
                await instance.storage.InitTableAsync()
                    .WithTimeout(initTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException te)
            {
                string errorMsg = String.Format("Unable to create or connect to the Azure table {0} in {1}", 
                    instance.TableName, initTimeout);
                instance.logger.Error(ErrorCode.AzureTable_32, errorMsg, te);
                throw new OrleansException(errorMsg, te);
            }
            catch (Exception ex)
            {
                string errorMsg = String.Format("Exception trying to create or connect to Azure table {0} : {1}", 
                    instance.TableName, ex.Message);
                instance.logger.Error(ErrorCode.AzureTable_33, errorMsg, ex);
                throw new OrleansException(errorMsg, ex);
            }
            return instance;
        }

       
        internal async Task<GossipTableEntry> ReadConfigurationEntryAsync()
        {
            var result = await storage.ReadSingleTableEntryAsync(this.GlobalServiceId, GossipTableEntry.CONFIGURATION_ROW).ConfigureAwait(false);
            return result != null ? result.Item1 : null;
        }

        internal async Task<GossipTableEntry> ReadGatewayEntryAsync(GatewayEntry gateway)
        {
            var result = await storage.ReadSingleTableEntryAsync(this.GlobalServiceId, GossipTableEntry.ConstructRowKey(gateway.SiloAddress, gateway.ClusterId)).ConfigureAwait(false);

            if (result != null)
            {
                var tableEntry = result.Item1;
                try
                {
                    tableEntry.ParseSiloAddressFromRowKey();
                    return tableEntry;
                }
                catch (Exception exc)
                {
                    logger.Error(
                        ErrorCode.AzureTable_61,
                        string.Format("Intermediate error parsing GossipTableEntry: {0}. Ignoring this entry.", tableEntry),
                        exc);
                }
            }

            return null;
        }

        internal async Task<Tuple<GossipTableEntry, Dictionary<SiloAddress, GossipTableEntry>>> ReadAllEntriesAsync()
        {
            var queryResults = await storage.ReadAllTableEntriesForPartitionAsync(this.GlobalServiceId).ConfigureAwait(false);

            // organize the returned storage entries by what they represent
            GossipTableEntry configInStorage = null;
            var gatewayInfoInStorage = new Dictionary<SiloAddress, GossipTableEntry>();

            foreach (var x in queryResults)
            {
                var tableEntry = x.Item1;

                if (tableEntry.RowKey.Equals(GossipTableEntry.CONFIGURATION_ROW))
                {
                    configInStorage = tableEntry;
                }
                else
                {
                    try
                    {
                        tableEntry.ParseSiloAddressFromRowKey();
                        gatewayInfoInStorage.Add(tableEntry.SiloAddress, tableEntry);
                    }
                    catch (Exception exc)
                    {
                        logger.Error(
                            ErrorCode.AzureTable_61,
                            string.Format("Intermediate error parsing GossipTableEntry: {0}. Ignoring this entry.", tableEntry),
                            exc);
                    }
                }
            }

            return new Tuple<GossipTableEntry, Dictionary<SiloAddress, GossipTableEntry>>(configInStorage, gatewayInfoInStorage);
        }


        internal async Task<bool> TryCreateConfigurationEntryAsync(MultiClusterConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException("configuration");

            var entry = new GossipTableEntry
            {
                PartitionKey = GlobalServiceId,
                RowKey = GossipTableEntry.CONFIGURATION_ROW,
                GossipTimestamp = configuration.AdminTimestamp,
                Clusters = string.Join(GossipTableEntry.ClustersListSeparator, configuration.Clusters),
                Comment = configuration.Comment ?? ""
            };

            return (await TryCreateTableEntryAsync(entry).ConfigureAwait(false));
        }

        internal async Task<bool> TryUpdateConfigurationEntryAsync(MultiClusterConfiguration configuration, GossipTableEntry entry, string eTag)
        {
            if (configuration == null) throw new ArgumentNullException("configuration");

            entry.GossipTimestamp = configuration.AdminTimestamp;
            entry.Clusters = string.Join(GossipTableEntry.ClustersListSeparator, configuration.Clusters);
            entry.Comment = configuration.Comment ?? "";

            return (await TryUpdateTableEntryAsync(entry, eTag).ConfigureAwait(false));
        }

        internal async Task<bool> TryCreateGatewayEntryAsync(GatewayEntry entry)
        {
            var row = new GossipTableEntry()
            {
                PartitionKey = GlobalServiceId,
                RowKey = GossipTableEntry.ConstructRowKey(entry.SiloAddress, entry.ClusterId),
                Status = entry.Status.ToString(),
                GossipTimestamp = entry.HeartbeatTimestamp
            };

            return (await TryCreateTableEntryAsync(row).ConfigureAwait(false));
        }


        internal async Task<bool> TryUpdateGatewayEntryAsync(GatewayEntry entry, GossipTableEntry row, string eTag)
        {            
            row.Status = entry.Status.ToString();
            row.GossipTimestamp = entry.HeartbeatTimestamp;

            return (await TryUpdateTableEntryAsync(row, eTag).ConfigureAwait(false));
        }

        internal Task<bool> TryDeleteGatewayEntryAsync(GossipTableEntry row, string eTag)
        {
            return TryDeleteTableEntryAsync(row, eTag);
        }

        internal async Task<int> DeleteTableEntries()
        {
            var entries = await storage.ReadAllTableEntriesForPartitionAsync(GlobalServiceId).ConfigureAwait(false);
            var entriesList = new List<Tuple<GossipTableEntry, string>>(entries);
            if (entriesList.Count <= AzureTableDefaultPolicies.MAX_BULK_UPDATE_ROWS)
            {
                await storage.DeleteTableEntriesAsync(entriesList).ConfigureAwait(false);
            }
            else
            {
                List<Task> tasks = new List<Task>();
                foreach (var batch in entriesList.BatchIEnumerable(AzureTableDefaultPolicies.MAX_BULK_UPDATE_ROWS))
                {
                    tasks.Add(storage.DeleteTableEntriesAsync(batch));
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            return entriesList.Count;
        }


        /// <summary>
        /// Try once to conditionally update a data entry in the Azure table. Returns false if etag does not match.
        /// </summary>
        private async Task<bool> TryUpdateTableEntryAsync(GossipTableEntry data, string dataEtag, [CallerMemberName]string operation = null)
        {
            return await TryOperation(() => storage.UpdateTableEntryAsync(data, dataEtag), operation);
        }

        /// <summary>
        /// Try once to insert a new data entry in the Azure table. Returns false if there is a conflict.
        /// </summary>       
        private async Task<bool> TryCreateTableEntryAsync(GossipTableEntry data, [CallerMemberName]string operation = null)
        {
            return await TryOperation(() => storage.CreateTableEntryAsync(data), operation);
        }

        /// <summary>
        /// Try once to delete a data entry in the Azure table. Returns false if there is a conflict.
        /// </summary>       
        private async Task<bool> TryDeleteTableEntryAsync(GossipTableEntry data, string etag, [CallerMemberName]string operation = null)
        {
            return await TryOperation(() => storage.DeleteTableEntryAsync(data, etag), operation);
        }

        private async Task<bool> TryOperation(Func<Task> func, string operation = null)
        {
            try
            {
                await func().ConfigureAwait(false);
                return true;
            }
            catch (Exception exc)
            {
                HttpStatusCode httpStatusCode;
                string restStatus;
                if (!AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus)) throw;

                if (logger.IsVerbose2) logger.Verbose2("{0} failed with httpStatusCode={1}, restStatus={2}", operation, httpStatusCode, restStatus);
                if (AzureStorageUtils.IsContentionError(httpStatusCode)) return false;

                throw;
            }
        }

    }
}
