using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.MultiCluster;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MultiClusterNetwork;

namespace Orleans.Clustering.DynamoDB.MultiClusterNetwork
{
    class DynamoDBBasedGossipChannel : IGossipChannel
    {
        private readonly ILogger _logger;
        private static int _sequenceNumber;
        private GossipTableInstanceManager _tableManager;

        public string Name { get; }
        private readonly ILoggerFactory _loggerFactory;
        
        public DynamoDBBasedGossipChannel(ILoggerFactory loggerFactory)
        {
            Name = "DynamoDBBasedGossipChannel-" + ++_sequenceNumber;
            _logger = loggerFactory.CreateLogger<DynamoDBBasedGossipChannel>();
            _loggerFactory = loggerFactory;
        }

        public async Task Initialize(string serviceId, string connectionString)
        {
            _logger.Info("Initializing Gossip Channel for ServiceId={0} using connection: {1}",
                serviceId, ConfigUtilities.RedactConnectionStringInfo(connectionString));

            var builder = new StorageProvider(connectionString);

            await Initialize(serviceId, builder);
        }

        internal async Task Initialize(string serviceId, IStorageProvider provider)
        {
            _tableManager = await GossipTableInstanceManager.GetManager(serviceId, _loggerFactory, provider);
        }

        public async Task Publish(IMultiClusterGossipData data)
        {
            _logger.Debug("-Publish data:{0}", data);
            // this is (almost) always called with just one item in data to be written back
            // so we are o.k. with doing individual tasks for each storage read and write

            if (data.Configuration != null)
            {
                var configInStorage = await _tableManager.ReadConfigurationEntryAsync();
                await DiffAndWriteBackConfigAsync(data.Configuration, configInStorage);
            }
            foreach (var gateway in data.Gateways.Values)
            {
                var gatewayInfoInStorage = await _tableManager.ReadGatewayEntryAsync(gateway);
                await DiffAndWriteBackGatewayInfoAsync(gateway, gatewayInfoInStorage);
            }
        }

        // compare config with configInStorage, and
        // - write config to storage if it is newer (or do nothing on etag conflict)
        // - return config from store if it is newer
        private async Task<MultiClusterConfiguration> DiffAndWriteBackConfigAsync(MultiClusterConfiguration config, GossipConfiguration configInStorage)
        {
            if (config != null &&
                (configInStorage == null || configInStorage.GossipTimestamp < config.AdminTimestamp))
            {
                // push the more recent configuration to storage
                if (configInStorage == null)
                    await _tableManager.TryCreateConfigurationEntryAsync(config);
                else
                    await _tableManager.TryUpdateConfigurationEntryAsync(config, configInStorage);
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
        private async Task<GatewayEntry> DiffAndWriteBackGatewayInfoAsync(GatewayEntry gatewayInfo, GossipGateway gatewayInfoInStorage)
        {
            if ((gatewayInfo != null && !gatewayInfo.Expired)
                && (gatewayInfoInStorage == null || gatewayInfoInStorage.GossipTimestamp < gatewayInfo.HeartbeatTimestamp))
            {
                // push  the more recent gateway info to storage
                if (gatewayInfoInStorage == null)
                {
                    await _tableManager.TryCreateGatewayEntryAsync(gatewayInfo);
                }
                else
                {
                    await _tableManager.TryUpdateGatewayEntryAsync(gatewayInfo, gatewayInfoInStorage);
                }
            }
            else if (gatewayInfoInStorage != null &&
                     (gatewayInfo == null || gatewayInfo.HeartbeatTimestamp < gatewayInfoInStorage.GossipTimestamp))
            {
                var fromstorage = gatewayInfoInStorage.ToGatewayEntry();
                if (fromstorage.Expired)
                {
                    // remove gateway info from storage
                    await _tableManager.TryDeleteGatewayEntryAsync(gatewayInfoInStorage);
                }
                else
                {
                    // pull the more recent info from storage
                    return fromstorage;
                }
            }
            return null;
        }

        public async Task<IMultiClusterGossipData> Synchronize(IMultiClusterGossipData gossipdata)
        {
            _logger.Debug("-Synchronize pushed:{0}", gossipdata);

            try
            {
                // read the entire gossip content from storage
                var configInStorage = await _tableManager.ReadConfigurationEntryAsync();
                var gatewayInfoInStorage = await _tableManager.ReadGatewayEntriesAsync();

                // diff and write back configuration
                var configDeltaTask = DiffAndWriteBackConfigAsync(gossipdata.Configuration, configInStorage);

                // diff and write back gateway info for each gateway appearing locally or in storage
                var gatewayDeltaTasks = new List<Task<GatewayEntry>>();
                var allAddresses = gatewayInfoInStorage.Keys.Union(gossipdata.Gateways.Keys);

                foreach (var address in allAddresses)
                {
                    gossipdata.Gateways.TryGetValue(address, out var pushedInfo);
                    gatewayInfoInStorage.TryGetValue(address, out var infoInStorage);

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

                _logger.Debug("-Synchronize pulled delta:{0}", delta);

                return delta;
            }
            catch (Exception e)
            {
                _logger.Info("-Synchronize encountered exception {0}", e);

                throw;
            }
        }
    }
}
