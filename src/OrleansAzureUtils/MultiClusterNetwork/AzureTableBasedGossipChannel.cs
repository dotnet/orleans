using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.MultiCluster;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.MultiClusterNetwork
{
    /// <summary>
    /// An implementation of a gossip channel based on a standard Orleans Azure table.
    /// Multiple gossip networks can use the same table, and are separated by partition key = GlobalServiceId
    /// </summary>
    internal class AzureTableBasedGossipChannel : IGossipChannel
    {
        private Logger logger;
        private GossipTableInstanceManager tableManager;
        private static int sequenceNumber;

        public string Name { get; private set; }

        public async Task Initialize(Guid serviceid, string connectionstring)
        {
            Name = "AzureTableBasedGossipChannel-" + ++sequenceNumber;
            logger = LogManager.GetLogger(Name, LoggerType.Runtime);

            logger.Info("Initializing Gossip Channel for ServiceId={0} using connection: {1}, SeverityLevel={2}",
                serviceid, ConfigUtilities.RedactConnectionStringInfo(connectionstring), logger.SeverityLevel);

            tableManager = await GossipTableInstanceManager.GetManager(serviceid, connectionstring, logger);
        }

        // used by unit tests
        public Task DeleteAllEntries()
        {
            logger.Info("DeleteAllEntries");
            return tableManager.DeleteTableEntries();
        }


        #region IGossipChannel

        public async Task Publish(MultiClusterData data)
        {
            logger.Verbose("-Publish data:{0}", data);
            // this is (almost) always called with just one item in data to be written back
            // so we are o.k. with doing individual tasks for each storage read and write

            var tasks = new List<Task>();
            if (data.Configuration != null)
            {
                Func<Task> publishconfig = async () => {
                    var configInStorage = await tableManager.ReadConfigurationEntryAsync();
                    await DiffAndWriteBackConfigAsync(data.Configuration, configInStorage);
                };
                tasks.Add(publishconfig());    
            }
            foreach (var gateway in data.Gateways.Values)
            {
                Func<Task> publishgatewayinfo = async () => {
                    var gatewayInfoInStorage = await tableManager.ReadGatewayEntryAsync(gateway);
                    await DiffAndWriteBackGatewayInfoAsync(gateway, gatewayInfoInStorage);
                };
                tasks.Add(publishgatewayinfo());
            }
            await Task.WhenAll(tasks);
        }

        public async Task<MultiClusterData> Synchronize(MultiClusterData pushed)
        {
            logger.Verbose("-Synchronize pushed:{0}", pushed);

            try
            {
                // read the entire table from storage
                var entriesFromStorage = await tableManager.ReadAllEntriesAsync();
                var configInStorage = entriesFromStorage.Item1;
                var gatewayInfoInStorage = entriesFromStorage.Item2;

                // diff and write back configuration
                var configDeltaTask = DiffAndWriteBackConfigAsync(pushed.Configuration, configInStorage);

                // diff and write back gateway info for each gateway appearing locally or in storage
                var gatewayDeltaTasks = new List<Task<GatewayEntry>>();
                var allAddresses = gatewayInfoInStorage.Keys.Union(pushed.Gateways.Keys);
                foreach (var address in allAddresses)
                {
                    GatewayEntry pushedInfo = null;
                    pushed.Gateways.TryGetValue(address, out pushedInfo);
                    GossipTableEntry infoInStorage = null;
                    gatewayInfoInStorage.TryGetValue(address, out infoInStorage);

                    gatewayDeltaTasks.Add(DiffAndWriteBackGatewayInfoAsync(pushedInfo, infoInStorage));
                }

                // wait for all the writeback tasks to complete
                // these are not batched because we want them to fail individually on e-tag conflicts, not all
                await configDeltaTask;
                await Task.WhenAll(gatewayDeltaTasks);

                // assemble delta pieces
                var gw = new Dictionary<SiloAddress, GatewayEntry>();
                foreach (var t in gatewayDeltaTasks)
                {
                    var d = t.Result;
                    if (d != null)
                        gw.Add(d.SiloAddress, d);
                }
                var delta = new MultiClusterData(gw, configDeltaTask.Result);

                logger.Verbose("-Synchronize pulled delta:{0}", delta);

                return delta;
            }
            catch (Exception e)
            {
                logger.Info("-Synchronize encountered exception {0}", e);

                throw e;
            }
        }

        #endregion


        // compare config with configInStorage, and
        // - write config to storage if it is newer (or do nothing on etag conflict)
        // - return config from store if it is newer
        internal async Task<MultiClusterConfiguration> DiffAndWriteBackConfigAsync(MultiClusterConfiguration config, GossipTableEntry configInStorage)
        {

            // interpret empty admin timestamp by taking the azure table timestamp instead
            // this allows an admin to inject a configuration by editing table directly
            if (configInStorage != null && configInStorage.GossipTimestamp == default(DateTime))
                configInStorage.GossipTimestamp = configInStorage.Timestamp.UtcDateTime;

            if (config != null &&
                (configInStorage == null || configInStorage.GossipTimestamp < config.AdminTimestamp))
            {
                // push the more recent configuration to storage
                if (configInStorage == null)
                    await tableManager.TryCreateConfigurationEntryAsync(config);
                else
                    await tableManager.TryUpdateConfigurationEntryAsync(config, configInStorage, configInStorage.ETag);
            }
            else if (configInStorage != null &&
                 (config == null || config.AdminTimestamp < configInStorage.GossipTimestamp))
            {
                // pull the more recent configuration from storage
                return configInStorage.ToConfiguration();
            }
            return null;
        }

        // compare gatewayInfo with gatewayInfoInStorage, and
        // - write gatewayInfo to storage if it is newer (or do nothing on etag conflict)
        // - remove expired gateway info from storage
        // - return gatewayInfoInStorage if it is newer
        internal async Task<GatewayEntry> DiffAndWriteBackGatewayInfoAsync(GatewayEntry gatewayInfo, GossipTableEntry gatewayInfoInStorage)
        {
            if ((gatewayInfo != null && !gatewayInfo.Expired)
                 && (gatewayInfoInStorage == null || gatewayInfoInStorage.GossipTimestamp < gatewayInfo.HeartbeatTimestamp))
            {
                // push  the more recent gateway info to storage
                if (gatewayInfoInStorage == null)
                {
                    await tableManager.TryCreateGatewayEntryAsync(gatewayInfo);
                }
                else
                {
                    await tableManager.TryUpdateGatewayEntryAsync(gatewayInfo, gatewayInfoInStorage, gatewayInfoInStorage.ETag);
                }
            }
            else if (gatewayInfoInStorage != null &&
                    (gatewayInfo == null || gatewayInfo.HeartbeatTimestamp < gatewayInfoInStorage.GossipTimestamp))
            {
                var fromstorage = gatewayInfoInStorage.ToGatewayEntry();
                if (fromstorage.Expired)
                {
                    // remove gateway info from storage
                    await tableManager.TryDeleteGatewayEntryAsync(gatewayInfoInStorage, gatewayInfoInStorage.ETag);
                }
                else
                {
                    // pull the more recent info from storage
                    return fromstorage;
                }
            }
            return null;
        }

    }
}
